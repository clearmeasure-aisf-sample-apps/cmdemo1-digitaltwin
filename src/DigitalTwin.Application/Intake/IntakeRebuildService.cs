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
    IReadOnlyDictionary<string, string> FingerprintBefore,
    IReadOnlyDictionary<string, string> FingerprintAfter,
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> Failures);

/// <summary>
/// Rebuilds ALL twin state (event journal and every read model) purely from the raw intake table.
/// Runs under the projection gate, so the before/after fingerprints compare a quiescent model.
/// </summary>
public sealed record IntakeRebuildStatus(bool Running, DateTimeOffset? StartedAt, int RawEventsReplayedSoFar, IntakeRebuildReport? LastReport, string? LastError);

public sealed class IntakeRebuildService(
    IRawIntakeStore rawStore,
    ITwinStore store,
    IntakeEventApplier applier,
    ProjectionGate gate,
    IRealtimeEventPublisher realtime,
    TimeProvider timeProvider)
{
    private const int PageSize = 200;
    private readonly Lock _lock = new();
    private Task<IntakeRebuildReport>? _current;
    private DateTimeOffset? _startedAt;
    private int _progress;
    private IntakeRebuildReport? _lastReport;
    private string? _lastError;

    /// <summary>Starts a rebuild (or joins the running one). Detached from callers: an HTTP timeout never aborts it.</summary>
    public Task<IntakeRebuildReport> StartOrJoin()
    {
        lock (_lock)
        {
            if (_current is { IsCompleted: false })
            {
                return _current;
            }
            _current = Task.Run(async () =>
            {
                try
                {
                    var report = await RebuildFromRawAsync(CancellationToken.None);
                    _lastReport = report;
                    _lastError = null;
                    return report;
                }
                catch (Exception ex)
                {
                    _lastError = $"{ex.GetType().Name}: {ex.GetBaseException().Message}";
                    throw;
                }
            });
            return _current;
        }
    }

    public IntakeRebuildStatus GetStatus()
        => new(_current is { IsCompleted: false }, _startedAt, Volatile.Read(ref _progress), _lastReport, _lastError);

    public async Task<IntakeRebuildReport> RebuildFromRawAsync(CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var startedAt = timeProvider.GetUtcNow();
        _startedAt = startedAt;
        Volatile.Write(ref _progress, 0);
        var before = await store.GetReadModelFingerprintAsync(ct);

        var cleared = await store.ClearEventJournalAsync(ct);
        await store.ClearReadModelsAsync(ct);

        var replayed = 0;
        var rebuilt = 0;
        var failures = new List<string>();
        long after = 0;
        while (true)
        {
            var page = await rawStore.ReadPageAsync(after, PageSize, ct);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var raw in page)
            {
                after = raw.Sequence;
                // Rows still queued behind the gate are not in the "before" state; live processing applies them next.
                if (raw.ProcessedAt is null)
                {
                    continue;
                }
                try
                {
                    var (_, applied) = await applier.ApplyRawAsync(raw, live: false, ct);
                    rebuilt += applied.Count;
                    replayed++;
                    Interlocked.Increment(ref _progress);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failures.Add($"{raw.Id}: {ex.GetType().Name}: {ex.GetBaseException().Message}");
                }
            }
        }

        var afterFingerprint = await store.GetReadModelFingerprintAsync(ct);
        var differences = before.Keys.Union(afterFingerprint.Keys)
            .Where(k => !string.Equals(before.GetValueOrDefault(k), afterFingerprint.GetValueOrDefault(k), StringComparison.Ordinal))
            .Select(k => $"{k}: {before.GetValueOrDefault(k) ?? "-"} -> {afterFingerprint.GetValueOrDefault(k) ?? "-"}")
            .ToList();

        var finishedAt = timeProvider.GetUtcNow();
        await realtime.PublishStateRefreshedAsync("rebuilt-from-raw", finishedAt, ct);
        return new IntakeRebuildReport(
            startedAt, finishedAt, cleared, replayed, failures.Count, rebuilt,
            differences.Count == 0, before, afterFingerprint, differences, failures.Take(20).ToList());
    }
}
