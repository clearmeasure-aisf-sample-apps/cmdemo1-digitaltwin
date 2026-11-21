using DigitalTwin.Core.BuildAndArtifact;
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
    double? CostYesterdayUsd);

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
    IReadOnlyList<ArtifactPackageSnapshot> Artifacts);

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
