namespace DigitalTwin.Core.ReliabilityAndDrift;

public sealed class HealthProbeSnapshot
{
    public string TargetKey { get; set; } = string.Empty; // "{stage}:{deployable}"
    public string StageName { get; set; } = string.Empty;
    public string DeployableName { get; set; } = string.Empty;
    public string Hosting { get; set; } = "containerapp";
    public string Url { get; set; } = string.Empty;
    public string HealthPath { get; set; } = "/_healthcheck";
    public int HttpStatusCode { get; set; }
    public bool IsHealthy { get; set; }
    public int LatencyMs { get; set; }
    public string? ReportedVersion { get; set; }
    public string? ReportedCommit { get; set; }
    public string? DatabaseVersion { get; set; }
    public string? BuildFactsJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}

public enum DriftCategory
{
    VersionMismatch,
    MissingResource,
    RetiredResourcePresent,
    UnhealthyEndpoint,
    RevisionDegraded,
    PendingPromotion,
    WebhookGap
}

public enum DriftSeverity
{
    Info,
    Warning,
    Critical
}

public sealed class DriftFinding
{
    public string DriftKey { get; set; } = string.Empty;
    public DriftCategory Category { get; set; }
    public DriftSeverity Severity { get; set; }
    public string StageName { get; set; } = string.Empty;
    public string? DeployableName { get; set; }
    public string? ResourceId { get; set; }
    public string ExpectedValue { get; set; } = string.Empty;
    public string ObservedValue { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? RemediationHint { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
