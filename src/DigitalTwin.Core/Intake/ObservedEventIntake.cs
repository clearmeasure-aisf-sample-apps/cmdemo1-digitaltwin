using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Core.Intake;

/// <summary>
/// Publishes poller/reconciler observations through the same durable intake path as webhooks.
/// Events whose idempotency key is already in the journal are skipped before capture; the processor
/// still dedupes, so a race between a webhook and a poll is harmless either way.
/// </summary>
public sealed class ObservedEventIntake(IIntakePublisher publisher, ITwinStore store, TimeProvider timeProvider)
{
    private const int MaxEventsPerBatch = 200;

    public async Task<int> PublishAsync(
        IEnumerable<ObservedEvent> events,
        EventSourceKind source,
        IngestionChannel channel,
        CancellationToken ct)
    {
        var distinct = events
            .GroupBy(e => e.IdempotencyKey, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (distinct.Count == 0)
        {
            return 0;
        }

        var existing = await store.FindExistingIdempotencyKeysAsync(distinct.Select(e => e.IdempotencyKey).ToList(), ct);
        var fresh = distinct
            .Where(e => !existing.Contains(e.IdempotencyKey))
            .OrderBy(e => e.OccurredAt)
            .ToList();

        foreach (var chunk in fresh.Chunk(MaxEventsPerBatch))
        {
            await publisher.CaptureAsync(RawIntakeEvent.ForObservedBatch(chunk, source, channel, timeProvider.GetUtcNow()), ct);
        }

        return fresh.Count;
    }

    public Task<int> PublishAsync(ObservedEvent observedEvent, IngestionChannel channel, CancellationToken ct)
        => PublishAsync([observedEvent], observedEvent.Source, channel, ct);
}
