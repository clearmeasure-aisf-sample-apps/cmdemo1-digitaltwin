using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Core.Projections;

/// <summary>
/// The only way pollers change current state: they publish what they observed, and only material changes
/// (compared with the projection's last content hash) become state observation events in the durable intake.
/// </summary>
public sealed class StateObservationPublisher(IIntakePublisher publisher, ITwinStore store, TimeProvider timeProvider)
{
    private const int MaxEventsPerBatch = 200;

    /// <summary>Publishes changed entities of an upsert read model. Returns the number of changed entities.</summary>
    public async Task<int> PublishAsync<T>(StateKind kind, IEnumerable<T> items, EventSourceKind source, CancellationToken ct) where T : class
    {
        EnsureKind<T>(kind, StateMode.Upsert);
        var current = await store.GetProjectionHashesAsync(kind.Name, ct);
        var observedAt = timeProvider.GetUtcNow();

        var changed = new List<ObservedEvent>();
        foreach (var item in items.GroupBy(i => kind.Key(i), StringComparer.Ordinal).Select(g => g.Last()))
        {
            var key = kind.Key(item);
            var hash = StateObservations.ContentHash(kind, item);
            if (current.TryGetValue(key, out var existing) && existing == hash)
            {
                continue;
            }
            changed.Add(StateObservations.ToEvent(kind, key, item, hash, source, observedAt));
        }

        await CaptureAsync(changed, source, ct);
        return changed.Count;
    }

    /// <summary>Publishes the complete set for a scope of a replace-set read model when it changed.</summary>
    public async Task<bool> PublishSetAsync<T>(StateKind kind, string scope, IEnumerable<T> items, EventSourceKind source, CancellationToken ct) where T : class
    {
        EnsureKind<T>(kind, StateMode.ReplaceSet);
        var list = items.GroupBy(i => kind.Key(i), StringComparer.Ordinal).Select(g => g.Last()).OrderBy(i => kind.Key(i), StringComparer.Ordinal).ToList();
        var hash = StateObservations.SetHash(kind, list);
        var current = await store.GetProjectionHashesAsync(kind.Name, ct);
        if (current.TryGetValue(scope, out var existing) && existing == hash)
        {
            return false;
        }

        await CaptureAsync([StateObservations.ToEvent(kind, scope, list, hash, source, timeProvider.GetUtcNow())], source, ct);
        return true;
    }

    private async Task CaptureAsync(List<ObservedEvent> events, EventSourceKind source, CancellationToken ct)
    {
        foreach (var chunk in events.Chunk(MaxEventsPerBatch))
        {
            await publisher.CaptureAsync(RawIntakeEvent.ForObservedBatch(chunk, source, IngestionChannel.Poll, timeProvider.GetUtcNow()), ct);
        }
    }

    private static void EnsureKind<T>(StateKind kind, StateMode mode)
    {
        if (kind.ClrType != typeof(T) || kind.Mode != mode)
        {
            throw new ArgumentException($"State kind {kind.Name} is {kind.Mode} of {kind.ClrType.Name}, not {mode} of {typeof(T).Name}.", nameof(kind));
        }
    }
}
