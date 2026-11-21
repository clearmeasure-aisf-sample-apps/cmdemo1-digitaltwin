using DigitalTwin.Core.Ports;

namespace DigitalTwin.Application.Intake;

public sealed record IntakeRebuildReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int JournalEventsCleared,
    int RawEventsReplayed,
    int RawEventsFailed,
    int JournalEventsRebuilt,
    IReadOnlyList<string> Failures);

/// <summary>Rebuilds the event journal purely from the raw intake table (the system of record).</summary>
public sealed class IntakeRebuildService(
    IRawIntakeStore rawStore,
    ITwinStore store,
    IntakeEventApplier applier,
    IRealtimeEventPublisher realtime,
    TimeProvider timeProvider) : IDisposable
{
    private const int PageSize = 200;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<IntakeRebuildReport> RebuildFromRawAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var startedAt = timeProvider.GetUtcNow();
            var cleared = await store.ClearEventJournalAsync(ct);
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
                    try
                    {
                        var (_, applied) = await applier.ApplyRawAsync(raw, live: false, ct);
                        rebuilt += applied.Count;
                        replayed++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add($"{raw.Id}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            var finishedAt = timeProvider.GetUtcNow();
            await realtime.PublishStateRefreshedAsync("rebuilt-from-raw", finishedAt, ct);
            return new IntakeRebuildReport(startedAt, finishedAt, cleared, replayed, failures.Count, rebuilt, failures.Take(20).ToList());
        }
        finally
        {
            _gate.Release();
        }
    }
}
