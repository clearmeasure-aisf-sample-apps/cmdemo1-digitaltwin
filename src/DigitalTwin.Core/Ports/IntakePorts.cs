using DigitalTwin.Core.Intake;

namespace DigitalTwin.Core.Ports;

/// <summary>
/// Durably captures a raw event (raw row + queued message in one transaction) before the caller acknowledges it.
/// Throws when the capture could not be committed, so senders get a 5xx and redeliver.
/// </summary>
public interface IIntakePublisher
{
    Task<IntakeReceipt> CaptureAsync(RawIntakeEvent rawEvent, CancellationToken ct);
}

public interface IRawIntakeStore
{
    Task<RawIntakeEvent?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RawIntakeEvent>> ReadPageAsync(long afterSequence, int take, CancellationToken ct);
    Task MarkProcessedAsync(Guid id, int appliedCount, DateTimeOffset processedAt, CancellationToken ct);
    Task RecordFailureAsync(Guid id, string failure, bool deadLettered, DateTimeOffset failedAt, CancellationToken ct);
    Task<RawIntakeStats> GetStatsAsync(CancellationToken ct);

    // Retention: whole UTC days of processed rows move to the archive, then leave SQL.
    /// <summary>UTC days that hold processed rows and end at or before <paramref name="before"/>.</summary>
    Task<IReadOnlyList<DateOnly>> GetArchivableDaysAsync(DateTimeOffset before, CancellationToken ct);
    /// <summary>Processed rows received on a UTC day, in capture order, after <paramref name="afterSequence"/>.</summary>
    Task<IReadOnlyList<RawIntakeEvent>> ReadProcessedDayPageAsync(DateOnly day, long afterSequence, int take, CancellationToken ct);
    Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    /// <summary>Space the raw table uses, in MB (0 where not measurable).</summary>
    Task<double> GetSizeMbAsync(CancellationToken ct);
}

/// <summary>
/// Long-term home of raw intake events older than the retention window: one compressed file per UTC day.
/// Together with the SQL raw table it holds the complete history a rebuild replays.
/// </summary>
public interface IRawIntakeArchive
{
    bool IsEnabled { get; }
    string Location { get; }
    Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct);
    IAsyncEnumerable<RawIntakeEvent> ReadDayAsync(DateOnly day, CancellationToken ct);
    /// <summary>Replaces the day's file with <paramref name="events"/> (written completely before it becomes visible).</summary>
    Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<RawIntakeEvent> events, CancellationToken ct);
}

/// <summary>Inbound port: applies one captured raw event to the twin model idempotently.</summary>
public interface IIntakeEventApplier
{
    Task<IntakeApplyResult> ApplyAsync(Guid rawEventId, CancellationToken ct);
}

public interface IIntakeQueueMonitor
{
    Task<IntakeQueueStatus> GetStatusAsync(CancellationToken ct);
}

public interface IDeadLetterAdmin
{
    Task<IReadOnlyList<DeadLetteredIntake>> ListAsync(int limit, CancellationToken ct);
    Task<int> ReplayAsync(IReadOnlyCollection<Guid>? envelopeIds, CancellationToken ct);
}
