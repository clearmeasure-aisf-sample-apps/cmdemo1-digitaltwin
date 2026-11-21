namespace DigitalTwin.Core.Observation;

public enum EventSourceKind
{
    GitHub,
    Octopus,
    Azure,
    GitOps,
    Probe,
    Twin
}

public enum IngestionChannel
{
    Webhook,
    Poll,
    Probe,
    Backfill
}

public enum EventOutcome
{
    None,
    Queued,
    Started,
    Waiting,
    Success,
    Failure,
    Error,
    Canceled,
    Skipped
}

/// <summary>
/// Append-only CloudEvents 1.0 + CDEvents-inspired normalized occurrence record.
/// Event identity for idempotency is (Source, SourceEventId) via IdempotencyKey.
/// </summary>
public sealed class ObservedEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public EventSourceKind Source { get; set; }
    public string SourceEventId { get; set; } = string.Empty;
    public string SpecVersion { get; set; } = "1.0";
    public string EventType { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string Predicate { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
    public IngestionChannel Channel { get; set; }
    public string? CorrelationId { get; set; }
    public string? ChainId { get; set; }
    public string? PipelineId { get; set; }
    public string? StageName { get; set; }
    public string? DeployableName { get; set; }
    public string? Actor { get; set; }
    public EventOutcome Outcome { get; set; } = EventOutcome.None;
    public string Summary { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed class IngestionCursor
{
    public string SourceKey { get; set; } = string.Empty;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastCursor { get; set; }
    public string Status { get; set; } = "pending";
    public string? LastError { get; set; }
    public long ItemsProcessed { get; set; }
}

public sealed class WebhookRegistrationStatus
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string EndpointUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? ExternalId { get; set; }
    public int? LastDeliveryStatusCode { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}
