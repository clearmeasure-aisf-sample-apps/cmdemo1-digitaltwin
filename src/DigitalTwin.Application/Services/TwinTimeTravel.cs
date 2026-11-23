using System.Collections.Concurrent;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Application.Services;

/// <summary>The read model a query runs against: live tables (<see cref="AsOf"/> null) or a point-in-time reconstruction.</summary>
public sealed record TwinReadScope(ITwinReadModel Model, DateTimeOffset Now, DateTimeOffset? AsOf);

/// <summary>Resolves "as of" timestamps to point-in-time read models folded from the journal's state observations.</summary>
public sealed class TwinTimeTravel(ITwinStore store, IJournalArchive archive, TimeProvider timeProvider)
{
    private static readonly TimeSpan ArchiveIndexLifetime = TimeSpan.FromMinutes(5);
    private (DateTimeOffset At, IReadOnlyList<DateOnly> Days, IReadOnlyList<DateOnly> Checkpoints, DateTimeOffset? HistoryStart)? _archiveIndex;
    private const int MaxCachedModels = 16;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HistoryStartLifetime = TimeSpan.FromMinutes(5);
    private (DateTimeOffset At, DateTimeOffset Start)? _historyStart;
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

    /// <summary>The first state observation. Read on every dashboard load; it only moves with a rebuild, so it is cached briefly once known.</summary>
    public async Task<DateTimeOffset?> GetHistoryStartAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (_historyStart is { } cached && now - cached.At < HistoryStartLifetime)
        {
            return cached.Start;
        }
        var sqlStart = await store.GetStateHistoryStartAsync(ct);
        var archivedStart = (await ArchiveIndexAsync(ct)).HistoryStart;
        var start = archivedStart is { } a && (sqlStart is null || a < sqlStart) ? a : sqlStart;
        _historyStart = start is { } known ? (now, known) : null;
        return start;
    }

    /// <summary>Called after the journal archive changes, so folds into newly archived days read the archive at once.</summary>
    public void InvalidateArchive()
    {
        _archiveIndex = null;
        _historyStart = null;
        _cache.Clear();
    }

    private async Task<(DateTimeOffset At, IReadOnlyList<DateOnly> Days, IReadOnlyList<DateOnly> Checkpoints, DateTimeOffset? HistoryStart)> ArchiveIndexAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (_archiveIndex is { } index && now - index.At < ArchiveIndexLifetime)
        {
            return index;
        }
        if (!archive.IsEnabled)
        {
            return (_archiveIndex = (now, [], [], null)).Value;
        }
        var days = await archive.ListDaysAsync(ct);
        var checkpoints = await archive.ListCheckpointsAsync(ct);
        var meta = days.Count > 0 ? await archive.ReadMetaAsync(ct) : null;
        return (_archiveIndex = (now, days, checkpoints, meta?.HistoryStart)).Value;
    }

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
        var index = await ArchiveIndexAsync(CancellationToken.None);
        if (index.Days.Count > 0 && at < JournalArchiveFormat.StartOf(index.Days[^1].AddDays(1)))
        {
            // Inside the archived window SQL keeps only each subject's newest observation as of the archive boundary:
            // the end-of-day checkpoint before this day plus the archived days since give the same fold as the full journal.
            var day = JournalArchiveFormat.DayOf(at);
            var checkpoint = index.Checkpoints.Where(c => c < day).Select(c => (DateOnly?)c).LastOrDefault();
            var archived = await JournalArchiveFormat.LatestStatePerSubjectAsync(ArchivedAsync(checkpoint, index.Days.Where(d => (checkpoint is null || d > checkpoint) && d <= day)), at, CancellationToken.None);
            latest = [.. archived, .. latest];
        }
        return PointInTimeReadModel.Build(at, latest, drift);
    }

    private async IAsyncEnumerable<ObservedEvent> ArchivedAsync(DateOnly? checkpoint, IEnumerable<DateOnly> days)
    {
        if (checkpoint is { } c)
        {
            await foreach (var ev in archive.ReadCheckpointAsync(c, CancellationToken.None))
            {
                yield return ev;
            }
        }
        foreach (var day in days)
        {
            await foreach (var ev in archive.ReadDayAsync(day, CancellationToken.None))
            {
                yield return ev;
            }
        }
    }
}
