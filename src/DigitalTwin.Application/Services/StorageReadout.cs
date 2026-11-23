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

public sealed record StorageJournalDto(long Rows, long StateRows, long DriftRows, long LifecycleRows, double SizeMb, IReadOnlyList<JournalKindUsage>? Kinds);

/// <param name="ProjectedPeakMb">Live database plus a shadow copy of every rebuilt table.</param>
public sealed record StorageRebuildDto(double ProjectedPeakMb, double BudgetMb, bool WithinBudget);

public sealed record StorageReadoutDto(DateTimeOffset At, StorageDatabaseDto Database, StorageRawDto RawIntake, StorageJournalDto Journal, StorageRebuildDto Rebuild, bool ArchiveEnabled);

/// <summary>Database, raw intake, journal and archive sizes, and whether a rebuild would fit the budget.</summary>
public sealed class StorageReadout(
    ITwinStore store,
    IRawIntakeStore rawStore,
    IRawIntakeArchive archive,
    IOptions<RetentionOptions> retention,
    IOptions<StorageOptions> options,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private (DateTimeOffset At, StorageReadoutDto Readout)? _cached;

    public async Task<StorageReadoutDto> GetAsync(bool detail, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (!detail && _cached is { } hit && now - hit.At < CacheFor)
        {
            return hit.Readout;
        }

        var database = await store.GetDatabaseUsageAsync(ct);
        var raw = await rawStore.GetStatsAsync(ct);
        var rawMb = await rawStore.GetSizeMbAsync(ct);
        var days = await archive.ListDaysAsync(ct);
        var journal = await store.GetJournalUsageAsync(detail, ct);

        var peak = Math.Round(database.UsedMb + journal.ModelTablesMb, 1);
        var readout = new StorageReadoutDto(
            now,
            new StorageDatabaseDto(database.UsedMb, database.MaxMb),
            new StorageRawDto(raw.Total, raw.Pending, raw.DeadLettered, Math.Round(rawMb, 1), retention.Value.MaxRawMb, days.Count, days.Count > 0 ? days[0] : null),
            new StorageJournalDto(journal.Rows, journal.StateRows, journal.DriftRows, journal.LifecycleRows, journal.TableMb, journal.Kinds),
            new StorageRebuildDto(peak, options.Value.RebuildPeakBudgetMb, peak <= options.Value.RebuildPeakBudgetMb),
            archive.IsEnabled);
        if (!detail)
        {
            _cached = (now, readout);
        }
        return readout;
    }
}
