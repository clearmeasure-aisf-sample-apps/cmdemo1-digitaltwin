using System.Runtime.CompilerServices;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using Microsoft.EntityFrameworkCore;

namespace DigitalTwin.Persistence;

/// <summary>Journal archiving support: day discovery, streaming a day out, and compacting what the archive now holds.</summary>
public sealed partial class EfTwinStore
{
    private const int CompactionBatch = 2000;

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

    public async IAsyncEnumerable<ObservedEvent> StreamJournalAsync(DateTimeOffset from, DateTimeOffset until, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        if (db.Database.IsSqlite())
        {
            var all = await db.ObservedEvents.AsNoTracking().ToListAsync(ct);
            foreach (var ev in all.Where(e => e.OccurredAt >= from && e.OccurredAt < until).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id))
            {
                yield return ev;
            }
            yield break;
        }
        await foreach (var ev in db.ObservedEvents.AsNoTracking().Where(e => e.OccurredAt >= from && e.OccurredAt < until)
                           .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return ev;
        }
    }

    public async Task<JournalCompaction> CompactJournalDayAsync(DateOnly day, JournalKeepRule keep, CancellationToken ct)
    {
        var start = JournalArchiveFormat.StartOf(day);
        var end = JournalArchiveFormat.StartOf(day.AddDays(1));
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        if (db.Database.IsSqlite())
        {
            return await CompactInMemoryAsync(db, start, end, keep, ct);
        }

        var table = JournalTable(db);
        // Superseded: a newer observation of the same subject before the end of the day exists, so no fold at or after
        // the end of the day can pick this row (ties at the newest instant are all kept; the fold breaks them by key).
        var stateSql =
            "DELETE TOP (" + CompactionBatch + ") e FROM " + table + " e WHERE e.EventType = {0} AND e.SubjectType <> {1} AND e.OccurredAt < {2} " +
            "AND EXISTS (SELECT 1 FROM " + table + " n WHERE n.EventType = {0} AND n.SubjectType = e.SubjectType AND n.SubjectId = e.SubjectId " +
            "AND n.OccurredAt > e.OccurredAt AND n.OccurredAt < {2})";
        var superseded = 0;
        int deleted;
        do
        {
            deleted = await db.Database.ExecuteSqlRawAsync(stateSql, [StateKinds.EventType, StateKinds.Drift.Name, end], ct);
            superseded += deleted;
        }
        while (deleted > 0);

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
        }
        while (deleted > 0);
        return new JournalCompaction(superseded, lifecycle);
    }

    public async Task<IReadOnlyList<DateOnly>> GetCompactableJournalDaysAsync(DateTimeOffset before, JournalKeepRule keep, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
        var lifecycle = db.ObservedEvents.AsNoTracking().Where(e => e.EventType != StateKinds.EventType);
        if (db.Database.IsSqlite())
        {
            var rows = await lifecycle.ToListAsync(ct);
            return rows.Where(e => e.OccurredAt < before && !keep.Keeps(e)).Select(e => JournalArchiveFormat.DayOf(e.OccurredAt)).Distinct().Order().ToList();
        }

        // Rows that arrived after their day was archived: only lifecycle events can (old source timestamps from backfills).
        var always = keep.Always.ToList();
        var failuresOnly = keep.FailuresOnly.ToList();
        var unconditional = keep.Unconditional.ToList();
        var deletable = lifecycle.Where(e => !((e.ChainId != null && (always.Contains(e.EventType) || (failuresOnly.Contains(e.EventType) && e.Outcome == EventOutcome.Failure)))
                                               || unconditional.Contains(e.EventType)));
        var days = new List<DateOnly>();
        DateTimeOffset? cursor = DateTimeOffset.MinValue;
        while (cursor is { } from && from < before)
        {
            var next = await deletable.Where(e => e.OccurredAt >= from && e.OccurredAt < before).MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
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
