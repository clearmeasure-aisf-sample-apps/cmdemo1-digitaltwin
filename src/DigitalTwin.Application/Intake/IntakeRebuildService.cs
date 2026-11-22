using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;

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
    double LivePausedMilliseconds = 0);

public sealed record IntakeRebuildStatus(
    bool Running,
    DateTimeOffset? StartedAt,
    int RawEventsReplayedSoFar,
    IntakeRebuildReport? LastReport,
    string? LastError,
    string? Phase = null);

/// <summary>
/// Rebuilds ALL twin state (event journal and every read model) from the raw intake history (archive + SQL) into shadow
/// tables while live intake keeps running. Events processed live meanwhile are caught up; the last round runs under the
/// projection gate (a pause of seconds), where shadow and live fingerprints are compared. The shadow replaces live state
/// atomically only if its read models are identical (or <c>force</c>); otherwise it is discarded. Interrupted at any
/// point before the swap, live state is untouched.
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
    TimeProvider timeProvider) : IDisposable
{
    private const int PageSize = 200;
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
        try
        {
            _phase = "preparing shadow tables";
            var shadow = await target.CreateAsync(ct);
            var run = new ReplayRun(shadow);

            _phase = "replaying history";
            await foreach (var page in history.ReadPagesAsync(PageSize, ct))
            {
                await ReplayAsync(run, page, ct);
            }

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
                catchUp += await CatchUpAsync(run, ct);
                live = await store.GetReadModelFingerprintAsync(ct);
                rebuilt = await shadow.GetReadModelFingerprintAsync(ct);
                differences = live.Keys.Union(rebuilt.Keys)
                    .Where(k => !string.Equals(live.GetValueOrDefault(k), rebuilt.GetValueOrDefault(k), StringComparison.Ordinal))
                    .Select(k => $"{k}: {live.GetValueOrDefault(k) ?? "-"} -> {rebuilt.GetValueOrDefault(k) ?? "-"}")
                    .ToList();
                readModelsIdentical = differences.All(d => d.StartsWith(JournalFingerprintKey + ":", StringComparison.Ordinal));
                journalReplaced = JournalCount(live);
                if (readModelsIdentical || force)
                {
                    await target.SwapAsync(ct);
                    swapped = true;
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
                Aborted: !swapped, Swapped: swapped, CatchUpEventsReplayed: catchUp, LivePausedMilliseconds: Math.Round(pausedMs));
        }
        finally
        {
            if (!swapped)
            {
                await target.DiscardAsync(CancellationToken.None);
            }
        }
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

    private static int JournalCount(IReadOnlyDictionary<string, string> fingerprint)
        => fingerprint.TryGetValue(JournalFingerprintKey, out var value) && int.TryParse(value.Split(':')[0], out var count) ? count : 0;
}
