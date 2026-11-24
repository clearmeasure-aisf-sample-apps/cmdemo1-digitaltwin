using System.Runtime.CompilerServices;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Intake;

public sealed class JournalRetentionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Days of journal kept whole in SQL (ADR-035): two weeks of every event for investigations.</summary>
    public int Days { get; set; } = 14;

    /// <summary>
    /// Size budget for the journal table: while it is larger, the oldest complete days are archived early (never today).
    /// At current volume this, not <see cref="Days"/>, decides how much stays in SQL, like the raw intake budget.
    /// </summary>
    public double MaxJournalMb { get; set; } = 250;
}

/// <summary><paramref name="FetchedFromSql"/>: rows read out of SQL for the day file (rows already in it are not read again).</summary>
public sealed record JournalRetentionDay(DateOnly Day, int Archived, int FetchedFromSql, int CheckpointSubjects, int SupersededStateRemoved, int LifecycleRemoved, string Reason);

public sealed record JournalRetentionReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Archive,
    string? Skipped,
    IReadOnlyList<JournalRetentionDay> Days,
    double JournalMbBefore,
    double JournalMbAfter);

/// <summary>
/// Moves whole UTC days of the event journal into the archive (one file per day of occurrence, merged with any earlier
/// file, read back and checked), writes the day's end-of-day state checkpoint, then compacts SQL: state observations a
/// newer one supersedes are removed (drift history stays whole), and lifecycle events leave unless change chains, DORA
/// or config views read them. Time travel into archived days folds the checkpoint and day files (ADR-035).
/// </summary>
public sealed class JournalRetentionService(
    ITwinStore store,
    IJournalArchive archive,
    MaintenanceLock maintenance,
    TwinTimeTravel timeTravel,
    IOptions<JournalRetentionOptions> options,
    TimeProvider timeProvider)
{
    /// <summary>Lifecycle events SQL keeps after archiving (chain signals, config changes).</summary>
    public static readonly JournalKeepRule Keep = new(
        Always: ChangeChainSignals.EventTypes.Where(t => t is not "dev.cdevents.taskRun.finished" and not "dev.cdevents.build.finished").ToList(),
        FailuresOnly: ["dev.cdevents.taskRun.finished", "dev.cdevents.build.finished"],
        Unconditional: TwinQueryService.ConfigChangeEventTypes);

    private readonly Lock _lock = new();
    private Task<JournalRetentionReport>? _current;

    public JournalRetentionReport? LastReport { get; private set; }

    public string? LastError { get; private set; }

    public bool Running => _current is { IsCompleted: false };

    /// <summary>What the running pass is doing (day and step), so a long run on a busy tier can be followed.</summary>
    public string? Progress { get; private set; }

    /// <summary>Starts a run (or joins the running one), detached from callers: an HTTP timeout never cancels it.</summary>
    public Task<JournalRetentionReport> StartOrJoin(double? olderThanDays)
    {
        lock (_lock)
        {
            if (_current is { IsCompleted: false })
            {
                return _current;
            }
            _current = Task.Run(async () =>
            {
                try
                {
                    var report = await RunAsync(olderThanDays, CancellationToken.None);
                    LastError = null;
                    return report;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.GetBaseException().Message}";
                    throw;
                }
            });
            return _current;
        }
    }

    public async Task<JournalRetentionReport> RunAsync(double? olderThanDays, CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow();
        var sizeBefore = await JournalMbAsync(ct);
        JournalRetentionReport Done(string? skipped, IReadOnlyList<JournalRetentionDay> days, double sizeAfter)
            => LastReport = new JournalRetentionReport(startedAt, timeProvider.GetUtcNow(), archive.Location, skipped, days, sizeBefore, sizeAfter);

        if (!archive.IsEnabled)
        {
            return Done("no archive configured: the journal is kept whole in SQL", [], sizeBefore);
        }
        using var held = await maintenance.TryEnterAsync("journal-retention", TimeSpan.Zero, ct);
        if (held is null)
        {
            return Done($"{maintenance.Holder} is running", [], sizeBefore);
        }

        var meta = await archive.ReadMetaAsync(ct) ?? new JournalArchiveMeta(await store.GetStateHistoryStartAsync(ct));
        var results = new List<JournalRetentionDay>();
        try
        {
            var archived = (await archive.ListDaysAsync(ct)).ToHashSet();
            var through = meta.ArchivedThrough ?? (archived.Count > 0 ? JournalArchiveFormat.StartOf(archived.Max().AddDays(1)) : DateTimeOffset.MinValue);

            // Rows that arrived after their day was archived (backfilled lifecycle events with old timestamps).
            Progress = "looking for rows that arrived after their day was archived";
            foreach (var day in await store.GetCompactableJournalDaysAsync(through, Keep, ct))
            {
                results.Add(await ArchiveAsync(day, through, archived.Contains(day), "late", ct));
                archived.Add(day);
            }

            // Age keeps whole days; over the size budget everything but the last hour goes (the day it ends in is merged
            // again by the next run), so a single busy day cannot hold the database above budget until midnight.
            var target = JournalArchiveFormat.StartOf(JournalArchiveFormat.DayOf(startedAt.AddDays(-(olderThanDays ?? options.Value.Days))));
            var reason = "age";
            if (olderThanDays is not null)
            {
                target = startedAt.AddDays(-olderThanDays.Value);
            }
            else if (await JournalMbAsync(ct) > options.Value.MaxJournalMb)
            {
                target = startedAt - RecentKeptWhole;
                reason = "size";
            }

            if (target > through)
            {
                var from = through == DateTimeOffset.MinValue ? through : JournalArchiveFormat.StartOf(JournalArchiveFormat.DayOf(through));
                foreach (var day in await store.GetJournalDaysAsync(from, target, ct))
                {
                    results.Add(await ArchiveAsync(day, target, archived.Contains(day), reason, ct));
                    archived.Add(day);
                }
                meta = meta with { ArchivedThrough = target };
            }
            if (meta.HistoryStart is not null || meta.ArchivedThrough is not null)
            {
                await archive.WriteMetaAsync(meta, ct);
            }
        }
        finally
        {
            Progress = null;
            timeTravel.InvalidateArchive();
        }
        return Done(null, results, await JournalMbAsync(ct));
    }

    /// <summary>How much of the newest journal stays whole in SQL even when the size budget forces archiving.</summary>
    public static readonly TimeSpan RecentKeptWhole = TimeSpan.FromHours(1);

    /// <summary>Archives what occurred on <paramref name="day"/> before <paramref name="until"/>, checkpoints it, then compacts SQL.</summary>
    private async Task<JournalRetentionDay> ArchiveAsync(DateOnly day, DateTimeOffset until, bool alreadyArchived, string reason, CancellationToken ct)
    {
        var start = JournalArchiveFormat.StartOf(day);
        var end = JournalArchiveFormat.StartOf(day.AddDays(1));
        if (until < end)
        {
            end = until;
        }

        var label = $"{day:yyyy-MM-dd} ({reason})";
        var fromSql = new HashSet<string>(StringComparer.Ordinal);
        var written = await archive.WriteDayAsync(day, MergedAsync(day, alreadyArchived, start, end, fromSql, label, ct), ct);
        Progress = $"{label}: verifying the day file";
        var check = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var ev in archive.ReadDayAsync(day, ct))
        {
            check.Add(ev.IdempotencyKey);
        }
        if (check.Count != written || !fromSql.IsSubsetOf(check))
        {
            throw new InvalidOperationException($"Journal archive verification failed for {day:yyyy-MM-dd}: wrote {written}, read back {check.Count}.");
        }

        // The fold input for "as of the end of what is archived": SQL still holds every subject's newest observation before it.
        Progress = $"{label}: writing the checkpoint";
        var checkpoint = await store.GetLatestStateEventsAsync(end.AddTicks(-1), ct);
        var subjects = await archive.WriteCheckpointAsync(day, checkpoint.ToAsyncEnumerable(), ct);
        if (subjects != checkpoint.Count)
        {
            throw new InvalidOperationException($"Journal checkpoint verification failed for {day:yyyy-MM-dd}: wrote {subjects} of {checkpoint.Count}.");
        }

        Progress = $"{label}: compacting SQL";
        var compaction = await store.CompactJournalAsync(start, end, Keep, ct);
        if (compaction.SupersededStateRows + compaction.LifecycleRows > 1000)
        {
            Progress = $"{label}: refreshing statistics";
            await store.RefreshJournalStatisticsAsync(ct);
        }
        return new JournalRetentionDay(day, written, fromSql.Count, subjects, compaction.SupersededStateRows, compaction.LifecycleRows, reason);
    }

    private async IAsyncEnumerable<ObservedEvent> MergedAsync(DateOnly day, bool alreadyArchived, DateTimeOffset start, DateTimeOffset end,
        HashSet<string> fromSql, string label, [EnumeratorCancellation] CancellationToken ct)
    {
        Progress = $"{label}: writing the day file";
        var archivedIds = new HashSet<long>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (alreadyArchived)
        {
            await foreach (var ev in archive.ReadDayAsync(day, ct))
            {
                archivedIds.Add(ev.Id);
                if (keys.Add(ev.IdempotencyKey))
                {
                    yield return ev;
                }
            }
        }
        // A day archived before (a late row, or a run interrupted before compaction finished) is not read out of SQL again.
        // One entry per idempotency key: polls re-deliver events compaction removed, and SQL takes them in again as new rows.
        await foreach (var ev in store.StreamJournalAsync(start, end, archivedIds, ct))
        {
            fromSql.Add(ev.IdempotencyKey);
            if (fromSql.Count % 1000 == 0)
            {
                Progress = $"{label}: writing the day file, {fromSql.Count} rows read from SQL";
            }
            if (keys.Add(ev.IdempotencyKey))
            {
                yield return ev;
            }
        }
    }

    private async Task<double> JournalMbAsync(CancellationToken ct)
        => (await store.GetDatabaseUsageAsync(ct)).LargestTablesMb.GetValueOrDefault("dbo.ObservedEvents");
}
