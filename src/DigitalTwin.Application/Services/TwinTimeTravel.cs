using System.Collections.Concurrent;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Application.Services;

/// <summary>The read model a query runs against: live tables (<see cref="AsOf"/> null) or a point-in-time reconstruction.</summary>
public sealed record TwinReadScope(ITwinReadModel Model, DateTimeOffset Now, DateTimeOffset? AsOf);

/// <summary>Resolves "as of" timestamps to point-in-time read models folded from the journal's state observations.</summary>
public sealed class TwinTimeTravel(ITwinStore store, TimeProvider timeProvider)
{
    private const int MaxCachedModels = 16;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<long, (DateTimeOffset BuiltAt, Lazy<Task<PointInTimeReadModel>> Model)> _cache = new();

    public async Task<TwinReadScope> ResolveAsync(DateTimeOffset? asOf, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (asOf is null || asOf.Value >= now)
        {
            return new TwinReadScope(store, now, null);
        }

        var at = asOf.Value.ToUniversalTime();
        return new TwinReadScope(await GetModelAsync(at, now), at, at);
    }

    public Task<DateTimeOffset?> GetHistoryStartAsync(CancellationToken ct) => store.GetStateHistoryStartAsync(ct);

    private Task<PointInTimeReadModel> GetModelAsync(DateTimeOffset at, DateTimeOffset now)
    {
        foreach (var stale in _cache.Where(c => now - c.Value.BuiltAt > CacheLifetime).Select(c => c.Key).ToList())
        {
            _cache.TryRemove(stale, out _);
        }
        if (_cache.Count >= MaxCachedModels)
        {
            _cache.Clear();
        }

        // Built without the caller's token: a model shared by concurrent requests must not fail because one disconnects.
        var entry = _cache.GetOrAdd(at.UtcTicks, _ => (now, new Lazy<Task<PointInTimeReadModel>>(() => BuildAsync(at))));
        var model = entry.Model.Value;
        if (model.IsFaulted || model.IsCanceled)
        {
            _cache.TryRemove(at.UtcTicks, out _);
        }
        return model;
    }

    private async Task<PointInTimeReadModel> BuildAsync(DateTimeOffset at)
    {
        var latest = await store.GetLatestStateEventsAsync(at, CancellationToken.None);
        var drift = await store.GetStateEventHistoryAsync(StateKinds.Drift.Name, at, CancellationToken.None);
        return PointInTimeReadModel.Build(at, latest, drift);
    }
}
