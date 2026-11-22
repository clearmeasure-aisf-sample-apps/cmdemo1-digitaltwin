using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Measurement;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Application.Models;

public sealed record TwinSummaryDto(
    string PlatformName,
    string SystemSlug,
    string SystemName,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PipelineSummaryDto> Pipelines,
    IReadOnlyList<StageSummaryDto> Stages,
    DoraMetricsSnapshot Dora7Days,
    DoraMetricsSnapshot Dora28Days,
    WorkBoardSummary WorkSummary,
    int ActiveDriftCount,
    int CriticalDriftCount,
    IReadOnlyList<IngestionCursor> IngestionCursors,
    IReadOnlyList<WebhookRegistrationStatus> Webhooks,
    GitHubRateLimitStatus GitHubQuota,
    IntakeQueueStatus IntakeQueue,
    double? CostMonthToDateUsd,
    double? CostYesterdayUsd,
    DateTimeOffset? AsOf = null);

public sealed record PipelineSummaryDto(
    PipelineDefinition Definition,
    IReadOnlyDictionary<string, string?> StageVersions,
    PipelineRunSnapshot? LatestRun,
    int OpenPrCount,
    int ActiveDeliveryCount,
    RepositorySnapshot? Repository);

public sealed record StageSummaryDto(
    DeploymentStageDefinition Stage,
    EnvironmentInstanceSnapshot? Instance,
    IReadOnlyList<WorkloadRevisionSnapshot> Workloads,
    IReadOnlyList<HealthProbeSnapshot> HealthProbes,
    IReadOnlyList<DeliveryExecutionSnapshot> RecentDeliveries,
    IReadOnlyList<DriftFinding> ActiveDrifts);

public sealed record TwinGraphDto(
    string PlatformName,
    string SystemSlug,
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges);

public sealed record GraphNodeDto(
    string Id,
    string Kind,
    string Label,
    string? Sublabel,
    string Status,
    IReadOnlyDictionary<string, string?> Metadata);

public sealed record GraphEdgeDto(
    string SourceId,
    string TargetId,
    string Relation);

public sealed record DashboardStateDto(
    TwinSummaryDto Summary,
    IReadOnlyList<ObservedEvent> RecentEvents,
    IReadOnlyList<WorkItemSnapshot> WorkItems,
    IReadOnlyList<SourceChangeSnapshot> OpenPullRequests,
    IReadOnlyList<PipelineRunSnapshot> RecentRuns,
    IReadOnlyList<DeliveryExecutionSnapshot> RecentDeliveries,
    IReadOnlyList<RunbookExecutionSnapshot> RecentRunbooks,
    IReadOnlyList<DeploymentFreezeSnapshot> Freezes,
    IReadOnlyList<DriftFinding> ActiveDrifts,
    IReadOnlyList<AzureResourceSnapshot> AzureResources,
    IReadOnlyList<ArtifactPackageSnapshot> Artifacts,
    DateTimeOffset? HistoryStartsAt = null,
    IReadOnlyList<ChangeChain>? ChangeChains = null,
    ChainDoraMetrics? ChainDora7Days = null,
    ChainDoraMetrics? ChainDora28Days = null);

public sealed record CausalExplanationDto(
    string Query,
    bool Found,
    string Summary,
    WorkItemSnapshot? WorkItem,
    IReadOnlyList<SourceChangeSnapshot> PullRequests,
    IReadOnlyList<PipelineRunSnapshot> PipelineRuns,
    IReadOnlyList<ReleaseSnapshot> Releases,
    IReadOnlyList<DeliveryExecutionSnapshot> Deliveries,
    IReadOnlyList<WorkloadRevisionSnapshot> Workloads,
    IReadOnlyList<ObservedEvent> TimelineEvents);

/// <summary>Compact answer to "what did the twin know at this instant?"</summary>
public sealed record PointInTimeStateDto(
    DateTimeOffset AsOf,
    DateTimeOffset? HistoryStartsAt,
    bool BeforeHistory,
    IReadOnlyList<StageAtDto> Environments,
    IReadOnlyList<AzureResourceAtDto> AzureResources,
    IReadOnlyDictionary<string, int> BoardColumns,
    int OpenWorkItems,
    IReadOnlyList<string> ActiveDrift);

public sealed record StageAtDto(
    string Stage,
    IReadOnlyDictionary<string, string?> DeployedVersions,
    IReadOnlyList<WorkloadAtDto> Workloads,
    DeliveryAtDto? LastDeployment);

public sealed record WorkloadAtDto(string Deployable, string? RunningVersion, string? Revision, string? Health);

public sealed record DeliveryAtDto(string Release, string? Deployable, string State, DateTimeOffset QueuedAt, DateTimeOffset? FinishedAt);

public sealed record AzureResourceAtDto(string Name, string Type, string ResourceGroup);

/// <summary>Change chains matching a query (or the most recent ones), with the journal events of the best match.</summary>
public sealed record ChangeChainsDto(
    DateTimeOffset? AsOf,
    string? Query,
    int MatchCount,
    IReadOnlyList<ChangeChain> Chains,
    IReadOnlyList<ObservedEvent> TimelineEvents);

/// <summary>An Octopus configuration change: deployment process, variables, project settings, triggers or runbooks.</summary>
public sealed record ConfigChangeDto(DateTimeOffset At, string Kind, string? Deployable, string? PipelineId, string Summary, string? Actor, string? Url);

/// <summary>Before/after comparison of two instants (from the event journal): what changed per environment and platform.</summary>
public sealed record EnvironmentComparisonDto(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<StageComparisonDto> Stages,
    IReadOnlyList<string> ResourcesAdded,
    IReadOnlyList<string> ResourcesRemoved,
    int ResourcesFrom,
    int ResourcesTo,
    IReadOnlyList<string> DriftFrom,
    IReadOnlyList<string> DriftTo,
    IReadOnlyList<ValueChangeDto> BoardColumns);

public sealed record StageComparisonDto(string Stage, IReadOnlyList<ValueChangeDto> Versions, IReadOnlyList<ValueChangeDto> Revisions, bool Changed);

public sealed record ValueChangeDto(string Name, string? From, string? To, bool Changed);

