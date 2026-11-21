namespace DigitalTwin.Core.RuntimeAndPlatform;

public sealed class DesiredSystemConfigSnapshot
{
    public string Id { get; set; } = "cmdemo1";
    public string SystemName { get; set; } = string.Empty;
    public string? SystemRepoSha { get; set; }
    public string SystemJson { get; set; } = "{}";
    public string PinnedVersionsJson { get; set; } = "{}";
    public string? NodesJson { get; set; }
    public string? CostJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EnvironmentInstanceSnapshot
{
    public string StageName { get; set; } = string.Empty; // tdd | uat | prod | pool
    public string Tier { get; set; } = string.Empty; // nonprod | prod
    public string HostingBoundary { get; set; } = string.Empty; // cae-cmdemo1-tdd | cae-cmdemo1-prod-ekdi | cae-cmdemo1-pool
    public string ResourceGroup { get; set; } = string.Empty;
    public bool IsDeclaredOnly { get; set; }
    public string? StackName { get; set; }
    public string? StackProvisioningState { get; set; }
    public string? SqlServerName { get; set; }
    public string? DatabaseName { get; set; }
    public string? KeyVaultName { get; set; }
    public int ResourceCount { get; set; }
    public int ActiveDriftCount { get; set; }
    public bool IsHealthy { get; set; }
    public double? CostMonthToDateUsd { get; set; }
    public double? CostYesterdayUsd { get; set; }
    public DateTimeOffset LastEvaluatedAt { get; set; }
}

public sealed class WorkloadRevisionSnapshot
{
    public string Id { get; set; } = string.Empty; // "{stage}:{deployable}"
    public string StageName { get; set; } = string.Empty;
    public string DeployableName { get; set; } = string.Empty;
    public string HostingType { get; set; } = "containerapp"; // containerapp | staticwebapp
    public string HostingBoundary { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string? Fqdn { get; set; }
    public string? PinnedVersion { get; set; }
    public string? RunningVersion { get; set; }
    public string? ActiveRevisionName { get; set; }
    public string? LatestReadyRevisionName { get; set; }
    public string? ProvisioningState { get; set; }
    public string? RunningState { get; set; }
    public string? HealthState { get; set; }
    public int? Replicas { get; set; }
    public int? MinReplicas { get; set; }
    public int? MaxReplicas { get; set; }
    public int? TrafficWeight { get; set; }
    public string? RunningImage { get; set; }
    public DateTimeOffset? RevisionCreatedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}

public sealed class AzureResourceSnapshot
{
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? StageName { get; set; }
    public string? DeployableName { get; set; }
    public string? ProvisioningState { get; set; }
    public string? ResourceHealthStatus { get; set; }
    public string? ResourceHealthSummary { get; set; }
    public string? Address { get; set; }
    public string? ManagedEnvironmentId { get; set; }
    public bool IsTwinOwnResource { get; set; }
    public string TagsJson { get; set; } = "{}";
    public DateTimeOffset ObservedAt { get; set; }
}
