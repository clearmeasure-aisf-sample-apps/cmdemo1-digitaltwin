using System.Runtime.CompilerServices;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Intake;

public sealed class RetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int Days { get; set; } = 30;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}

public sealed record RetentionDayResult(DateOnly Day, int Archived, int Pruned);

public sealed record RetentionReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    DateTimeOffset Cutoff,
    string Archive,
    string? Skipped,
    IReadOnlyList<RetentionDayResult> Days);

/// <summary>
/// Moves whole UTC days of processed raw intake events older than the retention window into the archive, then deletes
/// them from SQL. A day file is merged with any earlier file for that day, read back and checked before a single row is
/// deleted, so an interrupted run never loses events. Unprocessed and dead-lettered rows stay in SQL.
/// </summary>
public sealed class RawIntakeRetentionService(
    IRawIntakeStore rawStore,
    IRawIntakeArchive archive,
    MaintenanceLock maintenance,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider)
{
    private const int PageSize = 500;

    public RetentionReport? LastReport { get; private set; }

    public async Task<RetentionReport> RunAsync(double? olderThanDays, CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow();
        var cutoff = startedAt.AddDays(-(olderThanDays ?? options.Value.Days));
        RetentionReport Done(string? skipped, IReadOnlyList<RetentionDayResult> days)
            => LastReport = new RetentionReport(startedAt, timeProvider.GetUtcNow(), cutoff, archive.Location, skipped, days);

        if (!archive.IsEnabled)
        {
            return Done("no archive configured: raw events are kept in SQL", []);
        }
        using var held = await maintenance.TryEnterAsync("retention", TimeSpan.Zero, ct);
        if (held is null)
        {
            return Done($"{maintenance.Holder} is running", []);
        }

        var results = new List<RetentionDayResult>();
        var archivedDays = (await archive.ListDaysAsync(ct)).ToHashSet();
        foreach (var day in await rawStore.GetArchivableDaysAsync(cutoff, ct))
        {
            var fromSql = new HashSet<Guid>();
            var written = await archive.WriteDayAsync(day, MergedAsync(day, archivedDays.Contains(day), fromSql, ct), ct);

            var check = new HashSet<Guid>();
            await foreach (var ev in archive.ReadDayAsync(day, ct))
            {
                check.Add(ev.Id);
            }
            if (check.Count != written || !fromSql.IsSubsetOf(check))
            {
                throw new InvalidOperationException($"Archive verification failed for {day:yyyy-MM-dd}: wrote {written}, read back {check.Count}.");
            }
            var pruned = await rawStore.DeleteAsync(fromSql, ct);
            archivedDays.Add(day);
            results.Add(new RetentionDayResult(day, written, pruned));
        }
        return Done(null, results);
    }

    private async IAsyncEnumerable<RawIntakeEvent> MergedAsync(DateOnly day, bool alreadyArchived, HashSet<Guid> fromSql, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        if (alreadyArchived)
        {
            await foreach (var ev in archive.ReadDayAsync(day, ct))
            {
                seen.Add(ev.Id);
                yield return ev;
            }
        }
        long after = 0;
        while (true)
        {
            var page = await rawStore.ReadProcessedDayPageAsync(day, after, PageSize, ct);
            if (page.Count == 0)
            {
                yield break;
            }
            after = page[^1].Sequence;
            foreach (var ev in page)
            {
                fromSql.Add(ev.Id);
                if (seen.Add(ev.Id))
                {
                    yield return ev;
                }
            }
        }
    }
}
