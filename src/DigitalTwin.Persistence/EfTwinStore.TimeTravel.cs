using System.Runtime.CompilerServices;
using System.Text.Json;
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

        // Every row tied at the newest instant is kept; the fold breaks ties by idempotency key (ordinal).
        // Two steps: the newest ids come from the state-as-of index alone, then rows are fetched in short chunks by
        // clustered key. As one "WHERE Id IN (…)" statement the optimizer could scan the whole journal (stale statistics
        // after large compactions) and outlive the timeout on the Basic tier.
        var table = JournalTable(db);
        var ids = new List<long>();
        await foreach (var subject in StateSubjectsAsync(db, asOf, inclusive: true, ct))
        {
            var newest = subject[^1].OccurredAt;
            ids.AddRange(subject.Where(k => k.OccurredAt == newest).Select(k => k.Id));
        }
        var rowsSql = "SELECT e.* FROM " + table + " e JOIN OPENJSON({0}) WITH (Id bigint '$') j ON e.Id = j.Id";
        var events = new List<ObservedEvent>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
        {
            events.AddRange(await db.ObservedEvents.FromSqlRaw(rowsSql, JsonSerializer.Serialize(chunk)).AsNoTracking().ToListAsync(ct));
        }
        return events;
    }

    private sealed class StateKey
    {
        public long Id { get; set; }
        public string SubjectType { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public DateTimeOffset OccurredAt { get; set; }
    }

    /// <summary>
    /// State observations before <paramref name="until"/> (or at it, when <paramref name="inclusive"/>), one subject at a time, oldest first within a subject. Streamed
    /// in the state-as-of index's own order: a window function over it sorted or spooled in tempdb, and on the Basic tier's
    /// IO budget that single statement outlived its timeout whenever the index was not cached.
    /// </summary>
    private static async IAsyncEnumerable<List<StateKey>> StateSubjectsAsync(TwinDbContext db, DateTimeOffset until, bool inclusive,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sql = "SELECT Id, SubjectType, SubjectId, OccurredAt FROM " + JournalTable(db) +
                  " WHERE EventType = {0} AND OccurredAt " + (inclusive ? "<=" : "<") + " {1} ORDER BY SubjectType, SubjectId, OccurredAt";
        var subject = new List<StateKey>();
        await foreach (var key in db.Database.SqlQueryRaw<StateKey>(sql, StateKinds.EventType, until).AsAsyncEnumerable().WithCancellation(ct))
        {
            // The database collation is case-insensitive, so one subject's rows arrive together whatever their casing.
            if (subject.Count > 0 && !(string.Equals(subject[0].SubjectType, key.SubjectType, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(subject[0].SubjectId, key.SubjectId, StringComparison.OrdinalIgnoreCase)))
            {
                yield return subject;
                subject = [];
            }
            subject.Add(key);
        }
        if (subject.Count > 0)
        {
            yield return subject;
        }
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

    public async Task<IReadOnlyList<ObservedEvent>> GetEventsByChainAsync(IReadOnlyCollection<string> chainIds, DateTimeOffset? until, int limit, IReadOnlyCollection<string>? eventTypes, CancellationToken ct)
    {
        if (chainIds.Count == 0)
        {
            return [];
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        var ids = chainIds.ToList();
        IQueryable<ObservedEvent> query;
        if (db.Database.IsSqlServer())
        {
            // Hundreds of chain ids as one IN list made the optimizer scan the whole journal (payloads included) once its
            // statistics changed; OPENJSON is estimated small, so the ChainId index is seeked instead.
            var sql = "SELECT e.* FROM " + JournalTable(db) + " e JOIN OPENJSON({0}) WITH (Id nvarchar(200) '$') j ON e.ChainId = j.Id WHERE e.EventType <> {1}";
            query = db.ObservedEvents.FromSqlRaw(sql, JsonSerializer.Serialize(ids), StateKinds.EventType).AsNoTracking();
        }
        else
        {
            query = db.ObservedEvents.AsNoTracking().Where(e => e.ChainId != null && ids.Contains(e.ChainId) && e.EventType != StateKinds.EventType);
        }
        if (eventTypes is { Count: > 0 })
        {
            var types = eventTypes.ToList();
            query = query.Where(e => types.Contains(e.EventType));
        }
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

    public async Task<JournalUsage> GetJournalUsageAsync(bool detail, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!db.Database.IsSqlServer())
        {
            var all = await db.ObservedEvents.AsNoTracking().Select(e => new { e.EventType, e.SubjectType, e.Source, Bytes = e.PayloadJson.Length * 2 }).ToListAsync(ct);
            var stateRows = all.Count(e => e.EventType == StateKinds.EventType);
            var driftRows = all.Count(e => e.EventType == StateKinds.EventType && e.SubjectType == StateKinds.Drift.Name);
            return new JournalUsage(all.Count, stateRows, driftRows, all.Count - stateRows, 0, 0, detail
                ? all.GroupBy(e => e.EventType == StateKinds.EventType ? $"state:{e.SubjectType}" : $"lifecycle:{e.Source}")
                    .Select(g => new JournalKindUsage(g.Key, g.Count(), Math.Round(g.Sum(e => e.Bytes) / 1048576.0, 1)))
                    .OrderByDescending(k => k.PayloadMb).ToList()
                : null);
        }

        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var table = JournalTable(db);
        var schema = db.Model.FindEntityType(typeof(ObservedEvent))!.GetSchema() ?? "dbo";
        // Counts come from the (EventType, OccurredAt) and state-as-of indexes, sizes from allocation metadata: cheap enough for a readout.
        var countsSql =
            "SELECT (SELECT CAST(SUM(p.rows) AS bigint) FROM sys.partitions p WHERE p.object_id = OBJECT_ID({0}) AND p.index_id IN (0, 1)) AS Rows, " +
            "(SELECT COUNT_BIG(*) FROM " + table + " WHERE EventType = {1}) AS StateRows, " +
            "(SELECT COUNT_BIG(*) FROM " + table + " WHERE EventType = {1} AND SubjectType = {2}) AS DriftRows, " +
            "(SELECT CAST(ISNULL(SUM(a.used_pages), 0) * 8 / 1024.0 AS float) FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id WHERE p.object_id = OBJECT_ID({0})) AS TableMb, " +
            "(SELECT CAST(ISNULL(SUM(a.used_pages), 0) * 8 / 1024.0 AS float) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            "JOIN sys.partitions p ON p.object_id = t.object_id JOIN sys.allocation_units a ON a.container_id = p.partition_id WHERE s.name = {3}) AS ModelTablesMb";
        var counts = await db.Database.SqlQueryRaw<JournalCounts>(countsSql, $"[{schema}].[ObservedEvents]", StateKinds.EventType, StateKinds.Drift.Name, schema).SingleAsync(ct);

        List<JournalKindUsage>? kinds = null;
        if (detail)
        {
            var kindsSql =
                "SELECT CASE WHEN EventType = {0} THEN 'state:' + SubjectType ELSE 'lifecycle:' + CAST(Source AS nvarchar(8)) END AS Kind, COUNT_BIG(*) AS Rows, " +
                "CAST(SUM(CAST(DATALENGTH(PayloadJson) AS bigint)) / 1048576.0 AS float) AS PayloadMb FROM " + table +
                " GROUP BY CASE WHEN EventType = {0} THEN 'state:' + SubjectType ELSE 'lifecycle:' + CAST(Source AS nvarchar(8)) END";
            var rows = await db.Database.SqlQueryRaw<KindUsageRow>(kindsSql, StateKinds.EventType).ToListAsync(ct);
            kinds = rows.Select(r => new JournalKindUsage(LifecycleSourceName(r.Kind), r.Rows, Math.Round(r.PayloadMb, 1))).OrderByDescending(k => k.PayloadMb).ToList();
        }

        return new JournalUsage(counts.Rows, counts.StateRows, counts.DriftRows, counts.Rows - counts.StateRows,
            Math.Round(counts.TableMb, 1), Math.Round(counts.ModelTablesMb, 1), kinds);
    }

    private static string LifecycleSourceName(string kind)
        => kind.StartsWith("lifecycle:", StringComparison.Ordinal) && int.TryParse(kind["lifecycle:".Length..], System.Globalization.CultureInfo.InvariantCulture, out var source)
            ? $"lifecycle:{(EventSourceKind)source}"
            : kind;

    private sealed record JournalCounts(long Rows, long StateRows, long DriftRows, double TableMb, double ModelTablesMb);

    private sealed record KindUsageRow(string Kind, long Rows, double PayloadMb);

    private sealed record FileUsage(double UsedMb, double AllocatedMb, double MaxMb);

    private sealed record TableUsage(string Name, double UsedMb);
}
