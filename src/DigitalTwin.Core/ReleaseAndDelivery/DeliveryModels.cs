namespace DigitalTwin.Core.ReleaseAndDelivery;

public sealed class ReleaseSnapshot
{
    public string ReleaseId { get; set; } = string.Empty;
    public string ProjectSlug { get; set; } = string.Empty;
    public string? DeployableName { get; set; }
    public string PipelineId { get; set; } = string.Empty;
    public string ChannelName { get; set; } = "Default";
    public string Version { get; set; } = string.Empty;
    public string? GitCommitSha { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReleaseNotes { get; set; }
    public string ReleaseUrl { get; set; } = string.Empty;
}

public sealed class DeliveryExecutionSnapshot
{
    public string DeploymentId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string ProjectSlug { get; set; } = string.Empty;
    public string? DeployableName { get; set; }
    public string PipelineId { get; set; } = string.Empty;
    public string StageName { get; set; } = string.Empty; // tdd | uat | prod | pool-*
    public string ReleaseVersion { get; set; } = string.Empty;
    public string ChannelName { get; set; } = "Default";
    public string State { get; set; } = string.Empty; // queued | executing | waiting | succeeded | failed | canceled
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? StartedBy { get; set; }
    public string? SignedOffBy { get; set; }
    public string? SignOffReason { get; set; }
    public bool IsPersonSignOff { get; set; }
    public bool HasPendingInterruption { get; set; }
    public bool HasGuidedFailure { get; set; }
    public string? WaitsForKind { get; set; }
    public string? WaitsForTitle { get; set; }
    public string? WaitsForResponsible { get; set; }
    public string? GitCommitSha { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }
    public double? LeadTimeHours { get; set; }
    public string TaskUrl { get; set; } = string.Empty;
}

public sealed class RunbookExecutionSnapshot
{
    public string TaskId { get; set; } = string.Empty;
    public string RunbookName { get; set; } = string.Empty;
    public string ProjectSlug { get; set; } = string.Empty;
    public string StageName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Summary { get; set; }
    public string TaskUrl { get; set; } = string.Empty;
}

public sealed class DeploymentFreezeSnapshot
{
    public string FreezeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public bool IsActive { get; set; }
    public string EnvironmentsJson { get; set; } = "[]";
    public string ProjectsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
}
