using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using Microsoft.EntityFrameworkCore;

namespace DigitalTwin.Persistence;

/// <summary>Point-in-time access to the journal's state observations (time travel).</summary>
public sealed partial class EfTwinStore
{
    public async Task<IReadOnlyList<ObservedEvent>> GetLatestStateEventsAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (db.Database.IsSqlite())
        {
            // SQLite (unit tests) cannot compare DateTimeOffset server-side; the point-in-time fold picks the newest.
            var all = await db.ObservedEvents.AsNoTracking().Where(e => e.EventType == StateKinds.EventType).ToListAsync(ct);
            return all.Where(e => e.OccurredAt <= asOf).ToList();
        }

        // RANK keeps every row tied at the newest instant; the fold breaks ties by idempotency key (ordinal).
        return await db.ObservedEvents.FromSql($"""
            SELECT e.* FROM dbo.ObservedEvents e WHERE e.Id IN (
                SELECT r.Id FROM (
                    SELECT Id, RANK() OVER (PARTITION BY SubjectType, SubjectId ORDER BY OccurredAt DESC) AS Rk
                    FROM dbo.ObservedEvents
                    WHERE EventType = {StateKinds.EventType} AND OccurredAt <= {asOf}
                ) r WHERE r.Rk = 1)
            """).AsNoTracking().ToListAsync(ct);
    }

    public async Task<DateTimeOffset?> GetStateHistoryStartAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.ObservedEvents.AsNoTracking().Where(e => e.EventType == StateKinds.EventType);
        if (db.Database.IsSqlite())
        {
            var times = await query.Select(e => e.OccurredAt).ToListAsync(ct);
            return times.Count == 0 ? null : times.Min();
        }
        return await query.OrderBy(e => e.OccurredAt).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ObservedEvent>> GetStateEventHistoryAsync(string kind, DateTimeOffset asOf, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.ObservedEvents.AsNoTracking().Where(e => e.EventType == StateKinds.EventType && e.SubjectType == kind);
        var rows = db.Database.IsSqlite()
            ? (await query.ToListAsync(ct)).Where(e => e.OccurredAt <= asOf)
            : await query.Where(e => e.OccurredAt <= asOf).ToListAsync(ct);
        return rows.OrderBy(e => e.OccurredAt.UtcTicks).ThenBy(e => e.IdempotencyKey, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<ObservedEvent>> GetEventsByChainAsync(IReadOnlyCollection<string> chainIds, DateTimeOffset? until, int limit, CancellationToken ct)
    {
        if (chainIds.Count == 0)
        {
            return [];
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = chainIds.ToList();
        var query = db.ObservedEvents.AsNoTracking().Where(e => e.ChainId != null && ids.Contains(e.ChainId) && e.EventType != StateKinds.EventType);
        var rows = db.Database.IsSqlite() || until is null
            ? await query.ToListAsync(ct)
            : await query.Where(e => e.OccurredAt <= until.Value).ToListAsync(ct);
        return rows.Where(e => until is null || e.OccurredAt <= until)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(limit)
            .ToList();
    }
}
