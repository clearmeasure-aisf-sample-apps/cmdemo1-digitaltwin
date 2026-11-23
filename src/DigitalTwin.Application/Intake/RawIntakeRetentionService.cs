using System.Runtime.CompilerServices;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Intake;

public sealed class RetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int Days { get; set; } = 30;

    /// <summary>
    /// Size budget for the raw table: while it is larger, the oldest complete days move to the archive even inside the
    /// retention window, keeping the Basic tier database well under its cap.
    /// </summary>
    public double MaxRawMb { get; set; } = 350;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}

public sealed record RetentionDayResult(DateOnly Day, int Archived, int Pruned, string Reason);

public sealed record RetentionReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    DateTimeOffset Cutoff,
    string Archive,
    string? Skipped,
    IReadOnlyList<RetentionDayResult> Days,
    double RawSizeMbBefore = 0,
    double RawSizeMbAfter = 0);

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
    private readonly Lock _lock = new();
    private Task<RetentionReport>? _current;

    public RetentionReport? LastReport { get; private set; }

    public string? LastError { get; private set; }

    public bool Running => _current is { IsCompleted: false };

    /// <summary>Starts a run (or joins the running one), detached from callers: an HTTP timeout never cancels it.</summary>
    public Task<RetentionReport> StartOrJoin(double? olderThanDays)
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

    public async Task<RetentionReport> RunAsync(double? olderThanDays, CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow();
        var cutoff = startedAt.AddDays(-(olderThanDays ?? options.Value.Days));
        var sizeBefore = await rawStore.GetSizeMbAsync(ct);
        RetentionReport Done(string? skipped, IReadOnlyList<RetentionDayResult> days, double sizeAfter)
            => LastReport = new RetentionReport(startedAt, timeProvider.GetUtcNow(), cutoff, archive.Location, skipped, days, sizeBefore, sizeAfter);

        if (!archive.IsEnabled)
        {
            return Done("no archive configured: raw events are kept in SQL", [], sizeBefore);
        }
        using var held = await maintenance.TryEnterAsync("retention", TimeSpan.Zero, ct);
        if (held is null)
        {
            return Done($"{maintenance.Holder} is running", [], sizeBefore);
        }

        var results = new List<RetentionDayResult>();
        var archivedDays = (await archive.ListDaysAsync(ct)).ToHashSet();
        foreach (var day in await rawStore.GetArchivableDaysAsync(cutoff, ct))
        {
            results.Add(await ArchiveDayAsync(day, archivedDays, "age", ct));
        }

        // Size budget: never today's rows, oldest complete day first.
        var size = await rawStore.GetSizeMbAsync(ct);
        var today = new DateTimeOffset(startedAt.UtcDateTime.Date, TimeSpan.Zero);
        foreach (var day in await rawStore.GetArchivableDaysAsync(today, ct))
        {
            if (size <= options.Value.MaxRawMb)
            {
                break;
            }
            results.Add(await ArchiveDayAsync(day, archivedDays, "size", ct));
            size = await rawStore.GetSizeMbAsync(ct);
        }
        return Done(null, results, size);
    }

    private async Task<RetentionDayResult> ArchiveDayAsync(DateOnly day, HashSet<DateOnly> archivedDays, string reason, CancellationToken ct)
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
        return new RetentionDayResult(day, written, pruned, reason);
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
