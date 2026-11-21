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
