using DigitalTwin.Application.Intake;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Application.Services;

/// <summary>Space budgets that keep the twin on the Basic tier (2 GB) with headroom.</summary>
public sealed class StorageOptions
{
    /// <summary>Highest database size a rebuild may reach: live tables plus the shadow copy it builds beside them.</summary>
    public double RebuildPeakBudgetMb { get; set; } = 1500;
}

public sealed record StorageDatabaseDto(double UsedMb, double MaxMb);

public sealed record StorageRawDto(long Rows, long Pending, long DeadLettered, double SizeMb, double BudgetMb, int ArchiveDays, DateOnly? OldestArchivedDay);

/// <param name="ArchivedThrough">Last archived day: before its end SQL keeps each subject's newest state observation and the lifecycle events chains read.</param>
public sealed record StorageJournalDto(long Rows, long StateRows, long DriftRows, long LifecycleRows, double SizeMb, double BudgetMb, int ArchiveDays, DateOnly? ArchivedThrough,
    IReadOnlyList<JournalKindUsage>? Kinds);

/// <param name="ProjectedPeakMb">Live database plus a shadow copy of every rebuilt table.</param>
public sealed record StorageRebuildDto(double ProjectedPeakMb, double BudgetMb, bool WithinBudget);

public sealed record StorageReadoutDto(DateTimeOffset At, StorageDatabaseDto Database, StorageRawDto RawIntake, StorageJournalDto Journal, StorageRebuildDto Rebuild, bool ArchiveEnabled,
    IReadOnlyDictionary<string, long>? TimingsMs = null);

/// <summary>
/// Database, raw intake, journal and archive sizes, and whether a rebuild would fit the budget. Computed in the background
/// (one computation at a time) and served from the last result, so callers never wait on the database while it is busy.
/// </summary>
public sealed class StorageReadout(
    ITwinStore store,
    IRawIntakeStore rawStore,
    IRawIntakeArchive archive,
    IJournalArchive journalArchive,
    IOptions<RetentionOptions> retention,
    IOptions<JournalRetentionOptions> journalRetention,
    IOptions<StorageOptions> options,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private Task<StorageReadoutDto>? _refresh;

    /// <summary>The last computed readout, or null before the first one finished.</summary>
    public StorageReadoutDto? Cached { get; private set; }

    /// <summary>The cached readout (refreshed in the background when older than a minute); waits only for the very first one.</summary>
    public async Task<StorageReadoutDto> GetAsync(bool detail, CancellationToken ct)
    {
        if (detail)
        {
            return await ComputeAsync(detail: true, ct);
        }
        if (Cached is { } cached)
        {
            if (timeProvider.GetUtcNow() - cached.At >= RefreshAfter)
            {
                _ = Refresh();
            }
            return cached;
        }
        return await Refresh().WaitAsync(ct);
    }

    /// <summary>Starts a background computation unless one is running; never cancelled by a caller.</summary>
    public Task<StorageReadoutDto> Refresh()
    {
        lock (_lock)
        {
            if (_refresh is { IsCompleted: false })
            {
                return _refresh;
            }
            _refresh = Task.Run(async () =>
            {
                var readout = await ComputeAsync(detail: false, CancellationToken.None);
                Cached = readout;
                return readout;
            });
            return _refresh;
        }
    }

    private async Task<StorageReadoutDto> ComputeAsync(bool detail, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var timings = new Dictionary<string, long>(StringComparer.Ordinal);
        async Task<T> Timed<T>(string name, Func<Task<T>> step)
        {
            var started = timeProvider.GetTimestamp();
            var result = await step();
            timings[name] = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;
            return result;
        }

        var database = await Timed("database", () => store.GetDatabaseUsageAsync(ct));
        var raw = await Timed("rawStats", () => rawStore.GetStatsAsync(ct));
        var rawMb = await Timed("rawSize", () => rawStore.GetSizeMbAsync(ct));
        var days = await Timed("archiveDays", () => archive.ListDaysAsync(ct));
        var journal = await Timed("journal", () => store.GetJournalUsageAsync(detail, ct));
        var journalDays = await Timed("journalArchiveDays", () => journalArchive.ListDaysAsync(ct));

        var peak = Math.Round(database.UsedMb + journal.ModelTablesMb, 1);
        var readout = new StorageReadoutDto(
            now,
            new StorageDatabaseDto(database.UsedMb, database.MaxMb),
            new StorageRawDto(raw.Total, raw.Pending, raw.DeadLettered, Math.Round(rawMb, 1), retention.Value.MaxRawMb, days.Count, days.Count > 0 ? days[0] : null),
            new StorageJournalDto(journal.Rows, journal.StateRows, journal.DriftRows, journal.LifecycleRows, journal.TableMb, journalRetention.Value.MaxJournalMb,
                journalDays.Count, journalDays.Count > 0 ? journalDays[^1] : null, journal.Kinds),
            new StorageRebuildDto(peak, options.Value.RebuildPeakBudgetMb, peak <= options.Value.RebuildPeakBudgetMb),
            archive.IsEnabled,
            timings);
        return readout;
    }
}
