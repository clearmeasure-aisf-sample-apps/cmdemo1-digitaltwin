using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Application.Intake;

public sealed record IntakeRebuildReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int JournalEventsCleared,
    int RawEventsReplayed,
    int RawEventsFailed,
    int JournalEventsRebuilt,
    bool Identical,
    bool ReadModelsIdentical,
    IReadOnlyDictionary<string, string> FingerprintBefore,
    IReadOnlyDictionary<string, string> FingerprintAfter,
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> Failures,
    bool Aborted = false,
    RebuildPreflightReport? Preflight = null,
    bool Swapped = false,
    int CatchUpEventsReplayed = 0,
    double LivePausedMilliseconds = 0,
    IReadOnlyDictionary<string, double>? StepSeconds = null,
    DateTimeOffset? JournalArchivedThrough = null,
    JournalCompaction? ShadowCompaction = null);

public sealed record IntakeRebuildStatus(
    bool Running,
    DateTimeOffset? StartedAt,
    int RawEventsReplayedSoFar,
    IntakeRebuildReport? LastReport,
    string? LastError,
    string? Phase = null);

/// <summary>
/// Rebuilds ALL twin state (event journal and every read model) from the raw intake history (archive + SQL) into shadow
/// tables while live intake keeps running. The shadow journal is compacted to the journal archive boundary as the replay
/// goes (ADR-036), so the shadow never holds what the live journal has already archived. Events processed live meanwhile
/// are caught up; the journals are compared before the last round, which runs under the projection gate and compares only
/// the read models (a pause of seconds). The shadow replaces live state atomically only if its read models are identical
/// (or <c>force</c>); otherwise it is discarded. Interrupted at any point before the swap, live state is untouched.
/// </summary>
public sealed class IntakeRebuildService(
    RawIntakeHistory history,
    IRawIntakeStore rawStore,
    MaintenanceLock maintenance,
    ITwinStore store,
    IRebuildTarget target,
    IntakeEventApplier applier,
    ProjectionGate gate,
    IRealtimeEventPublisher realtime,
    IJournalArchive journalArchive,
    TimeProvider timeProvider) : IDisposable
{
    private const int PageSize = 200;
    private const int CompactEveryPages = 50;
    // Journal rows this recent can still be in flight between live and shadow when the journals are compared.
    private static readonly TimeSpan JournalComparisonLag = TimeSpan.FromMinutes(15);
    private const int MaxCatchUpRounds = 5;
    private const int SmallCatchUp = 50;
    // The journal can legitimately differ after a normalization change; read models must not.
    private const string JournalFingerprintKey = "journal";
    private readonly Lock _lock = new();
    private Task<IntakeRebuildReport>? _current;
    private CancellationTokenSource? _cancellation;
    private DateTimeOffset? _startedAt;
    private int _progress;
    private string? _phase;
    private IntakeRebuildReport? _lastReport;
    private string? _lastError;

    public bool IsRunning => _current is { IsCompleted: false };

    /// <summary>Starts a rebuild (or joins the running one). Detached from callers: an HTTP timeout never aborts it.</summary>
    public Task<IntakeRebuildReport> StartOrJoin(bool force = false)
    {
        lock (_lock)
        {
            if (_current is { IsCompleted: false })
            {
                return _current;
            }
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _current = Task.Run(async () =>
            {
                try
                {
                    var report = await RebuildFromRawAsync(force, token);
                    _lastReport = report;
                    _lastError = null;
                    return report;
                }
                catch (Exception ex) when (token.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    // A SQL command aborted by the cancel surfaces as SqlException ("Operation cancelled by user").
                    _lastError = "OperationCanceledException: Rebuild cancelled.";
                    throw new OperationCanceledException("Rebuild cancelled.", ex, token);
                }
                catch (Exception ex)
                {
                    _lastError = $"{ex.GetType().Name}: {ex.GetBaseException().Message}";
                    throw;
                }
                finally
                {
                    _phase = null;
                }
            });
            return _current;
        }
    }

    /// <summary>Requests a running rebuild to stop; its shadow is discarded and live state stays as it is.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_current is not { IsCompleted: false } || _cancellation is null)
            {
                return false;
            }
            _cancellation.Cancel();
            return true;
        }
    }

    public void Dispose() => _cancellation?.Dispose();

    public IntakeRebuildStatus GetStatus()
        => new(IsRunning, _startedAt, Volatile.Read(ref _progress), _lastReport, _lastError, _phase);

    /// <param name="force">Swap even when the rebuilt read models differ from live (rows only live has are lost).</param>
    public async Task<IntakeRebuildReport> RebuildFromRawAsync(bool force, CancellationToken ct)
    {
        if (!target.IsSupported)
        {
            throw new InvalidOperationException("Rebuilds need SQL Server (shadow tables and an atomic schema swap).");
        }
        using var maintenanceHeld = await maintenance.TryEnterAsync("rebuild", TimeSpan.FromMinutes(10), ct)
            ?? throw new InvalidOperationException($"Rebuild could not start: {maintenance.Holder} is running.");
        var startedAt = timeProvider.GetUtcNow();
        _startedAt = startedAt;
        Volatile.Write(ref _progress, 0);
        var swapped = false;
        var steps = new Dictionary<string, double>(StringComparer.Ordinal);
        var stepStarted = timeProvider.GetTimestamp();
        void Step(string name)
        {
            steps[name] = Math.Round(timeProvider.GetElapsedTime(stepStarted).TotalSeconds, 1);
            stepStarted = timeProvider.GetTimestamp();
        }
        try
        {
            _phase = "preparing shadow tables";
            var shadow = await target.CreateAsync(ct);
            var run = new ReplayRun(shadow);
            Step("prepare");

            var boundary = await JournalBoundaryAsync(ct);
            var compacted = new JournalCompaction(0, 0);
            async Task CompactShadowAsync()
            {
                if (boundary is { } through)
                {
                    var c = await shadow.CompactJournalAsync(DateTimeOffset.MinValue, through, JournalRetentionService.Keep, ct);
                    compacted = new JournalCompaction(compacted.SupersededStateRows + c.SupersededStateRows, compacted.LifecycleRows + c.LifecycleRows);
                }
            }

            _phase = "replaying history";
            var pages = 0;
            await foreach (var page in history.ReadPagesAsync(PageSize, ct))
            {
                await ReplayAsync(run, page, ct);
                if (++pages % CompactEveryPages == 0)
                {
                    await CompactShadowAsync();
                }
            }
            await CompactShadowAsync();

            Step("replay");
            _phase = "catching up";
            var catchUp = 0;
            for (var round = 0; round < MaxCatchUpRounds; round++)
            {
                var replayedNow = await CatchUpAsync(run, ct);
                catchUp += replayedNow;
                if (replayedNow < SmallCatchUp)
                {
                    break;
                }
            }

            Step("catchUp");
            _phase = "comparing journals";
            var journalBefore = timeProvider.GetUtcNow() - JournalComparisonLag;
            var compactedScope = boundary is { } b ? new JournalCompactionScope(b, JournalRetentionService.Keep) : null;
            var liveJournalTask = store.GetJournalFingerprintAsync(journalBefore, compactedScope, ct);
            var shadowJournalTask = shadow.GetJournalFingerprintAsync(journalBefore, compactedScope, ct);
            await Task.WhenAll(liveJournalTask, shadowJournalTask);
            var liveJournal = await liveJournalTask;
            var shadowJournal = await shadowJournalTask;
            var liveJournalRows = (int)(await store.GetJournalUsageAsync(detail: false, ct)).Rows;
            Step("journalFingerprints");

            _phase = "final catch-up and swap";
            IReadOnlyDictionary<string, string> live;
            IReadOnlyDictionary<string, string> rebuilt;
            List<string> differences;
            bool readModelsIdentical;
            int journalReplaced;
            double pausedMs;
            using (await gate.EnterAsync(ct))
            {
                var pauseStarted = timeProvider.GetTimestamp();
                stepStarted = pauseStarted;
                catchUp += await CatchUpAsync(run, ct);
                Step("finalCatchUp");
                // In parallel: each reads only its own schema, and this is the part of the swap that holds the gate.
                var liveTask = store.GetReadModelFingerprintAsync(ct, includeJournal: false);
                var shadowTask = shadow.GetReadModelFingerprintAsync(ct, includeJournal: false);
                await Task.WhenAll(liveTask, shadowTask);
                live = WithJournal(await liveTask, liveJournal);
                rebuilt = WithJournal(await shadowTask, shadowJournal);
                Step("fingerprints");
                differences = live.Keys.Union(rebuilt.Keys)
                    .Where(k => !string.Equals(live.GetValueOrDefault(k), rebuilt.GetValueOrDefault(k), StringComparison.Ordinal))
                    .Select(k => $"{k}: {live.GetValueOrDefault(k) ?? "-"} -> {rebuilt.GetValueOrDefault(k) ?? "-"}")
                    .ToList();
                readModelsIdentical = differences.All(d => d.StartsWith(JournalFingerprintKey + ":", StringComparison.Ordinal));
                journalReplaced = liveJournalRows;
                if (readModelsIdentical || force)
                {
                    await target.SwapAsync(ct);
                    swapped = true;
                    Step("swap");
                }
                pausedMs = timeProvider.GetElapsedTime(pauseStarted).TotalMilliseconds;
            }

            var finishedAt = timeProvider.GetUtcNow();
            if (swapped)
            {
                await realtime.PublishStateRefreshedAsync("rebuilt-from-raw", finishedAt, ct);
            }
            return new IntakeRebuildReport(
                startedAt, finishedAt, journalReplaced, run.Replayed, run.Failures.Count, run.Appended,
                differences.Count == 0, readModelsIdentical, live, rebuilt, differences, run.Failures.Take(20).ToList(),
                Aborted: !swapped, Swapped: swapped, CatchUpEventsReplayed: catchUp, LivePausedMilliseconds: Math.Round(pausedMs), StepSeconds: steps,
                JournalArchivedThrough: boundary, ShadowCompaction: compacted);
        }
        finally
        {
            if (!swapped)
            {
                await target.DiscardAsync(CancellationToken.None);
            }
        }
    }

    private static SortedDictionary<string, string> WithJournal(IReadOnlyDictionary<string, string> readModels, string journal)
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal) { [JournalFingerprintKey] = journal };
        foreach (var (key, value) in readModels)
        {
            all[key] = value;
        }
        return all;
    }

    /// <summary>Where the live journal is archived through; the shadow is compacted to the same instant.</summary>
    private async Task<DateTimeOffset?> JournalBoundaryAsync(CancellationToken ct)
    {
        if (!journalArchive.IsEnabled)
        {
            return null;
        }
        var meta = await journalArchive.ReadMetaAsync(ct);
        if (meta?.ArchivedThrough is { } through)
        {
            return through;
        }
        var days = await journalArchive.ListDaysAsync(ct);
        return days.Count > 0 ? JournalArchiveFormat.StartOf(days[^1].AddDays(1)) : null;
    }

    private sealed class ReplayRun(ITwinStore shadow)
    {
        public ITwinStore Shadow { get; } = shadow;
        public long HighWater { get; set; }
        public HashSet<Guid> Unprocessed { get; } = [];
        public List<string> Failures { get; } = [];
        public int Replayed { get; set; }
        public int Appended { get; set; }
    }

    private async Task ReplayAsync(ReplayRun run, IReadOnlyList<RawIntakeEvent> page, CancellationToken ct)
    {
        if (page.Count == 0)
        {
            return;
        }
        run.HighWater = Math.Max(run.HighWater, page.Max(r => r.Sequence));
        // Rows not yet processed live are not in the live state either; catch-up replays them once they are.
        foreach (var raw in page.Where(r => r.ProcessedAt is null))
        {
            run.Unprocessed.Add(raw.Id);
        }
        var processed = page.Where(r => r.ProcessedAt is not null).ToList();
        foreach (var raw in processed)
        {
            run.Unprocessed.Remove(raw.Id);
        }
        try
        {
            var result = await applier.ReplayAsync(processed, run.Shadow, ct);
            run.Replayed += result.Replayed;
            run.Appended += result.Appended;
            run.Failures.AddRange(result.Failures);
            Interlocked.Add(ref _progress, result.Replayed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            run.Failures.Add($"page after sequence {processed.FirstOrDefault()?.Sequence}: {ex.GetType().Name}: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Replays rows captured or processed live since the last pass; returns how many were replayed.</summary>
    private async Task<int> CatchUpAsync(ReplayRun run, CancellationToken ct)
    {
        var before = run.Replayed;
        while (true)
        {
            var page = await rawStore.ReadPageAsync(run.HighWater, PageSize, ct);
            if (page.Count == 0)
            {
                break;
            }
            await ReplayAsync(run, page, ct);
        }
        var nowProcessed = new List<RawIntakeEvent>();
        foreach (var id in run.Unprocessed.ToList())
        {
            if (await rawStore.GetAsync(id, ct) is { ProcessedAt: not null } raw)
            {
                nowProcessed.Add(raw);
            }
        }
        foreach (var chunk in nowProcessed.Chunk(PageSize))
        {
            await ReplayAsync(run, chunk, ct);
        }
        return run.Replayed - before;
    }

}
