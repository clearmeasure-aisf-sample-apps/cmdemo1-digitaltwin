using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Core.Ports;

public interface ITwinStore
{
    Task<bool> AppendEventIfNewAsync(ObservedEvent observedEvent, CancellationToken ct);
    Task<int> AppendEventsIfNewAsync(IEnumerable<ObservedEvent> observedEvents, CancellationToken ct);
    Task<IReadOnlyList<ObservedEvent>> AppendEventsReturnNewAsync(IEnumerable<ObservedEvent> observedEvents, CancellationToken ct);
    Task<IReadOnlySet<string>> FindExistingIdempotencyKeysAsync(IReadOnlyCollection<string> idempotencyKeys, CancellationToken ct);
    Task<int> ClearEventJournalAsync(CancellationToken ct);
    Task<IReadOnlyList<ObservedEvent>> QueryEventsAsync(
        DateTimeOffset? since,
        EventSourceKind? source,
        string? pipelineId,
        string? stageName,
        string? eventType,
        int limit,
        CancellationToken ct);

    Task UpsertCursorAsync(IngestionCursor cursor, CancellationToken ct);
    Task<IReadOnlyList<IngestionCursor>> GetCursorsAsync(CancellationToken ct);

    Task ReplaceWebhookStatusesAsync(string source, IReadOnlyList<WebhookRegistrationStatus> statuses, CancellationToken ct);
    Task<IReadOnlyList<WebhookRegistrationStatus>> GetWebhookStatusesAsync(CancellationToken ct);

    Task UpsertRepositoriesAsync(IReadOnlyList<RepositorySnapshot> repositories, CancellationToken ct);
    Task<IReadOnlyList<RepositorySnapshot>> GetRepositoriesAsync(CancellationToken ct);

    Task UpsertSourceChangesAsync(IReadOnlyList<SourceChangeSnapshot> changes, CancellationToken ct);
    Task<IReadOnlyList<SourceChangeSnapshot>> GetSourceChangesAsync(CancellationToken ct);

    Task UpsertPipelineRunsAsync(IReadOnlyList<PipelineRunSnapshot> runs, CancellationToken ct);
    Task<IReadOnlyList<PipelineRunSnapshot>> GetPipelineRunsAsync(int limit, CancellationToken ct);

    Task UpsertArtifactsAsync(IReadOnlyList<ArtifactPackageSnapshot> artifacts, CancellationToken ct);
    Task<IReadOnlyList<ArtifactPackageSnapshot>> GetArtifactsAsync(CancellationToken ct);

    Task UpsertWorkItemsAsync(IReadOnlyList<WorkItemSnapshot> items, CancellationToken ct);
    Task<IReadOnlyList<WorkItemSnapshot>> GetWorkItemsAsync(CancellationToken ct);

    Task UpsertReleasesAsync(IReadOnlyList<ReleaseSnapshot> releases, CancellationToken ct);
    Task<IReadOnlyList<ReleaseSnapshot>> GetReleasesAsync(int limit, CancellationToken ct);

    Task UpsertDeliveriesAsync(IReadOnlyList<DeliveryExecutionSnapshot> deliveries, CancellationToken ct);
    Task<IReadOnlyList<DeliveryExecutionSnapshot>> GetDeliveriesAsync(int limit, CancellationToken ct);

    Task UpsertRunbooksAsync(IReadOnlyList<RunbookExecutionSnapshot> runs, CancellationToken ct);
    Task<IReadOnlyList<RunbookExecutionSnapshot>> GetRunbooksAsync(int limit, CancellationToken ct);

    Task ReplaceFreezesAsync(IReadOnlyList<DeploymentFreezeSnapshot> freezes, CancellationToken ct);
    Task<IReadOnlyList<DeploymentFreezeSnapshot>> GetFreezesAsync(CancellationToken ct);

    Task UpsertDesiredConfigAsync(DesiredSystemConfigSnapshot config, CancellationToken ct);
    Task<DesiredSystemConfigSnapshot?> GetDesiredConfigAsync(CancellationToken ct);

    Task UpsertEnvironmentInstancesAsync(IReadOnlyList<EnvironmentInstanceSnapshot> stages, CancellationToken ct);
    Task<IReadOnlyList<EnvironmentInstanceSnapshot>> GetEnvironmentInstancesAsync(CancellationToken ct);

    Task ReplaceWorkloadsAsync(IReadOnlyList<WorkloadRevisionSnapshot> workloads, CancellationToken ct);
    Task<IReadOnlyList<WorkloadRevisionSnapshot>> GetWorkloadsAsync(CancellationToken ct);

    Task ReplaceAzureResourcesAsync(IReadOnlyList<AzureResourceSnapshot> resources, CancellationToken ct);
    Task<IReadOnlyList<AzureResourceSnapshot>> GetAzureResourcesAsync(CancellationToken ct);

    Task UpsertHealthProbesAsync(IReadOnlyList<HealthProbeSnapshot> probes, CancellationToken ct);
    Task<IReadOnlyList<HealthProbeSnapshot>> GetHealthProbesAsync(CancellationToken ct);

    Task ReconcileDriftFindingsAsync(IReadOnlyList<DriftFinding> evaluated, DateTimeOffset evaluatedAt, CancellationToken ct);
    Task<IReadOnlyList<DriftFinding>> GetDriftFindingsAsync(bool includeResolved, CancellationToken ct);
}

public interface IRealtimeEventPublisher
{
    Task PublishEventAsync(ObservedEvent observedEvent, CancellationToken ct);
    Task PublishStateRefreshedAsync(string reason, DateTimeOffset at, CancellationToken ct);
}

public interface IGitHubSyncService
{
    Task SyncAsync(bool fullBackfill, CancellationToken ct);
    Task SyncBoardOnlyAsync(CancellationToken ct);
    Task SyncRepositoryAsync(string repositoryName, CancellationToken ct);
    Task<IReadOnlyList<WebhookRegistrationStatus>> EnsureWebhooksRegisteredAsync(string twinPublicBaseUrl, CancellationToken ct);
}

public interface IGitHubRateLimitProvider
{
    GitHubRateLimitStatus GetStatus();
    bool IsRestPaused(out string reason);
    bool IsRestBackoff();
}

public interface IOctopusSyncService
{
    Task SyncAsync(bool fullBackfill, CancellationToken ct);
    Task<WebhookRegistrationStatus> EnsureSubscriptionRegisteredAsync(string twinPublicBaseUrl, CancellationToken ct);
}

public interface IAzureSyncService
{
    Task SyncAsync(bool fullBackfill, CancellationToken ct);
    Task<WebhookRegistrationStatus> TryEnsureEventGridSubscriptionsAsync(string twinPublicBaseUrl, CancellationToken ct);
}

public interface IHealthProbeService
{
    Task ProbeAllAsync(CancellationToken ct);
}
