using DigitalTwin.Core.Ports;
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
        // Folding every subject's newest observation takes seconds on the Basic tier, longer under load.
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        if (db.Database.IsSqlite())
        {
            // SQLite (unit tests) cannot compare DateTimeOffset server-side; the point-in-time fold picks the newest.
            var all = await db.ObservedEvents.AsNoTracking().Where(e => e.EventType == StateKinds.EventType).ToListAsync(ct);
            return all.Where(e => e.OccurredAt <= asOf).ToList();
        }

        // RANK keeps every row tied at the newest instant; the fold breaks ties by idempotency key (ordinal).
        var table = JournalTable(db);
        var sql = "SELECT e.* FROM " + table + " e WHERE e.Id IN (SELECT r.Id FROM (" +
                  "SELECT Id, RANK() OVER (PARTITION BY SubjectType, SubjectId ORDER BY OccurredAt DESC) AS Rk FROM " + table +
                  " WHERE EventType = {0} AND OccurredAt <= {1}) r WHERE r.Rk = 1)";
        return await db.ObservedEvents.FromSqlRaw(sql, StateKinds.EventType, asOf).AsNoTracking().ToListAsync(ct);
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
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
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

    public async Task<DatabaseUsage> GetDatabaseUsageAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!db.Database.IsSqlServer())
        {
            return new DatabaseUsage(0, 0, 0, new Dictionary<string, double>());
        }
        var files = await db.Database.SqlQueryRaw<FileUsage>("""
            SELECT CAST(SUM(FILEPROPERTY(name, 'SpaceUsed')) * 8 / 1024.0 AS float) AS UsedMb,
                   CAST(SUM(size) * 8 / 1024.0 AS float) AS AllocatedMb,
                   CAST(ISNULL(CAST(DATABASEPROPERTYEX(DB_NAME(), 'MaxSizeInBytes') AS bigint), 0) / 1048576.0 AS float) AS MaxMb
            FROM sys.database_files WHERE type = 0
            """).SingleAsync(ct);
        var tables = await db.Database.SqlQueryRaw<TableUsage>("""
            SELECT TOP 8 s.name + '.' + t.name AS Name, CAST(SUM(a.used_pages) * 8 / 1024.0 AS float) AS UsedMb
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.partitions p ON p.object_id = t.object_id
            JOIN sys.allocation_units a ON a.container_id = p.partition_id
            GROUP BY s.name, t.name
            ORDER BY UsedMb DESC
            """).ToListAsync(ct);
        return new DatabaseUsage(Math.Round(files.UsedMb, 1), Math.Round(files.AllocatedMb, 1), Math.Round(files.MaxMb, 1),
            tables.ToDictionary(t => t.Name, t => Math.Round(t.UsedMb, 1), StringComparer.Ordinal));
    }

    private sealed record FileUsage(double UsedMb, double AllocatedMb, double MaxMb);

    private sealed record TableUsage(string Name, double UsedMb);
}
