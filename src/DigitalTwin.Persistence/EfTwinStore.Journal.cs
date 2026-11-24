using System.Text.Json;
using System.Runtime.CompilerServices;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using Microsoft.EntityFrameworkCore;

namespace DigitalTwin.Persistence;

/// <summary>Journal archiving support: day discovery, streaming a day out, and compacting what the archive now holds.</summary>
public sealed partial class EfTwinStore
{
    // Small batches with low deadlock priority and a breather in between: live intake keeps writing the journal meanwhile.
    private const int CompactionBatch = 500;
    private const int StreamChunk = 500;
    private static readonly TimeSpan CompactionPause = TimeSpan.FromMilliseconds(250);

    public async Task<IReadOnlyList<DateOnly>> GetJournalDaysAsync(DateTimeOffset from, DateTimeOffset before, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        if (db.Database.IsSqlite())
        {
            var times = await db.ObservedEvents.AsNoTracking().Select(e => e.OccurredAt).ToListAsync(ct);
            return times.Where(t => t >= from && t < before).Select(JournalArchiveFormat.DayOf).Distinct().Order().ToList();
        }

        // One index seek per day on OccurredAt, instead of a DISTINCT over the whole journal.
        var days = new List<DateOnly>();
        var cursor = from;
        while (cursor < before)
        {
            var next = await db.ObservedEvents.AsNoTracking().Where(e => e.OccurredAt >= cursor && e.OccurredAt < before)
                .MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
            if (next is null)
            {
                break;
            }
            var day = JournalArchiveFormat.DayOf(next.Value);
            days.Add(day);
            cursor = JournalArchiveFormat.StartOf(day.AddDays(1));
        }
        return days;
    }

    public async IAsyncEnumerable<ObservedEvent> StreamJournalAsync(DateTimeOffset from, DateTimeOffset until, IReadOnlySet<long>? skip,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        if (db.Database.IsSqlite())
        {
            var all = await db.ObservedEvents.AsNoTracking().ToListAsync(ct);
            foreach (var ev in all.Where(e => e.OccurredAt >= from && e.OccurredAt < until && skip?.Contains(e.Id) != true).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id))
            {
                yield return ev;
            }
            yield break;
        }
        // One long query (key lookups for every payload, in occurrence order) outlived any timeout on a busy Basic tier:
        // read the ids from the narrow OccurredAt index first, then fetch rows in short chunks by clustered key.
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        var ids = await db.ObservedEvents.AsNoTracking().Where(e => e.OccurredAt >= from && e.OccurredAt < until)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
        if (skip is { Count: > 0 })
        {
            ids = ids.Where(id => !skip.Contains(id)).ToList();
        }
        var sql = "SELECT e.* FROM " + JournalTable(db) + " e JOIN OPENJSON({0}) WITH (Id bigint '$') j ON e.Id = j.Id";
        foreach (var chunk in ids.Chunk(StreamChunk))
        {
            var rows = await db.ObservedEvents.FromSqlRaw(sql, JsonSerializer.Serialize(chunk)).AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
            foreach (var id in chunk)
            {
                if (rows.TryGetValue(id, out var ev))
                {
                    yield return ev;
                }
            }
        }
    }

    public async Task<JournalCompaction> CompactJournalAsync(DateTimeOffset from, DateTimeOffset until, JournalKeepRule keep, CancellationToken ct)
    {
        var start = from;
        var end = until;
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        if (db.Database.IsSqlite())
        {
            return await CompactInMemoryAsync(db, start, end, keep, ct);
        }

        var table = JournalTable(db);
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY LOW", ct);
        // Superseded: a newer observation of the same subject before the end of the day exists, so no fold at or after
        // the end of the day can pick this row (ties at the newest instant are all kept; the fold breaks them by key).
        // A DELETE TOP with that EXISTS scanned the whole clustered index (payloads in-row) once per batch on a Basic
        // tier: pick the ids from the narrow (EventType, SubjectType, SubjectId, OccurredAt) index, then delete by key.
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        var supersededIds = new List<long>();
        await foreach (var subject in StateSubjectsAsync(db, end, inclusive: false, ct))
        {
            if (!string.Equals(subject[0].SubjectType, StateKinds.Drift.Name, StringComparison.OrdinalIgnoreCase))
            {
                var newest = subject[^1].OccurredAt;
                supersededIds.AddRange(subject.Where(k => k.OccurredAt < newest).Select(k => k.Id));
            }
        }
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var deleteSql = "DELETE e FROM " + table + " e JOIN OPENJSON({0}) WITH (Id bigint '$') j ON e.Id = j.Id OPTION (LOOP JOIN)";
        var superseded = 0;
        foreach (var chunk in supersededIds.Chunk(CompactionBatch))
        {
            superseded += await db.Database.ExecuteSqlRawAsync(deleteSql, [JsonSerializer.Serialize(chunk)], ct);
            await Task.Delay(CompactionPause, ct);
        }
        int deleted;

        var parameters = new List<object> { StateKinds.EventType, start, end, (int)EventOutcome.Failure };
        string In(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return "(NULL)";
            }
            var names = values.Select(v => { parameters.Add(v); return "{" + (parameters.Count - 1) + "}"; });
            return "(" + string.Join(", ", names) + ")";
        }
        var lifecycleSql =
            "DELETE TOP (" + CompactionBatch + ") FROM " + table + " WHERE EventType <> {0} AND OccurredAt >= {1} AND OccurredAt < {2} " +
            "AND NOT ((ChainId IS NOT NULL AND (EventType IN " + In(keep.Always) + " OR (EventType IN " + In(keep.FailuresOnly) + " AND Outcome = {3}))) " +
            "OR EventType IN " + In(keep.Unconditional) + ")";
        var lifecycle = 0;
        do
        {
            deleted = await db.Database.ExecuteSqlRawAsync(lifecycleSql, parameters, ct);
            lifecycle += deleted;
            await Task.Delay(CompactionPause, ct);
        }
        while (deleted > 0);
        return new JournalCompaction(superseded, lifecycle);
    }

    public async Task<IReadOnlyList<DateOnly>> GetCompactableJournalDaysAsync(DateTimeOffset before, JournalKeepRule keep, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var lifecycle = db.ObservedEvents.AsNoTracking().Where(e => e.EventType != StateKinds.EventType);
        if (db.Database.IsSqlite())
        {
            var rows = await lifecycle.ToListAsync(ct);
            return rows.Where(e => e.OccurredAt < before && !keep.Keeps(e)).Select(e => JournalArchiveFormat.DayOf(e.OccurredAt)).Distinct().Order().ToList();
        }

        // Rows that arrived after their day was archived: only lifecycle events can (old source timestamps from backfills).
        // Before the boundary only kept and late lifecycle rows remain, so their narrow columns are few and cheap to read
        // through the (EventType, OccurredAt) index; the keep rule is applied here rather than in the query.
        var remaining = await lifecycle.Where(e => e.OccurredAt < before)
            .Select(e => new { e.OccurredAt, e.EventType, e.ChainId, e.Outcome }).ToListAsync(ct);
        return remaining.Where(r => !keep.Keeps(new ObservedEvent { EventType = r.EventType, ChainId = r.ChainId, Outcome = r.Outcome }))
            .Select(r => JournalArchiveFormat.DayOf(r.OccurredAt)).Distinct().Order().ToList();
    }

    public async Task RefreshJournalStatisticsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!db.Database.IsSqlServer())
        {
            return;
        }
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var sql = "UPDATE STATISTICS " + JournalTable(db);
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private static async Task<JournalCompaction> CompactInMemoryAsync(TwinDbContext db, DateTimeOffset start, DateTimeOffset end, JournalKeepRule keep, CancellationToken ct)
    {
        var all = await db.ObservedEvents.ToListAsync(ct);
        var state = all.Where(e => StateObservations.IsStateEvent(e) && e.SubjectType != StateKinds.Drift.Name && e.OccurredAt < end).ToList();
        var newest = state.GroupBy(e => (e.SubjectType, Subject: e.SubjectId.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Max(e => e.OccurredAt));
        var superseded = state.Where(e => e.OccurredAt < newest[(e.SubjectType, e.SubjectId.ToUpperInvariant())]).ToList();
        var lifecycle = all.Where(e => !StateObservations.IsStateEvent(e) && e.OccurredAt >= start && e.OccurredAt < end && !keep.Keeps(e)).ToList();
        db.ObservedEvents.RemoveRange(superseded.Concat(lifecycle));
        await db.SaveChangesAsync(ct);
        return new JournalCompaction(superseded.Count, lifecycle.Count);
    }
}
