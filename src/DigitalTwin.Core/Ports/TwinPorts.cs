using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Core.Ports;

/// <summary>Current-state read models: the live projection tables, or a point-in-time reconstruction of them.</summary>
public interface ITwinReadModel
{
    Task<IReadOnlyList<IngestionCursor>> GetCursorsAsync(CancellationToken ct);

    Task<IReadOnlyList<WebhookRegistrationStatus>> GetWebhookStatusesAsync(CancellationToken ct);

    Task<IReadOnlyList<RepositorySnapshot>> GetRepositoriesAsync(CancellationToken ct);

    Task<IReadOnlyList<SourceChangeSnapshot>> GetSourceChangesAsync(CancellationToken ct);

    Task<IReadOnlyList<PipelineRunSnapshot>> GetPipelineRunsAsync(int limit, CancellationToken ct);

    Task<IReadOnlyList<ArtifactPackageSnapshot>> GetArtifactsAsync(CancellationToken ct);

    Task<IReadOnlyList<WorkItemSnapshot>> GetWorkItemsAsync(CancellationToken ct);

    Task<IReadOnlyList<ReleaseSnapshot>> GetReleasesAsync(int limit, CancellationToken ct);

    Task<IReadOnlyList<DeliveryExecutionSnapshot>> GetDeliveriesAsync(int limit, CancellationToken ct);

    Task<IReadOnlyList<RunbookExecutionSnapshot>> GetRunbooksAsync(int limit, CancellationToken ct);

    Task<IReadOnlyList<DeploymentFreezeSnapshot>> GetFreezesAsync(CancellationToken ct);

    Task<DesiredSystemConfigSnapshot?> GetDesiredConfigAsync(CancellationToken ct);

    Task<IReadOnlyList<EnvironmentInstanceSnapshot>> GetEnvironmentInstancesAsync(CancellationToken ct);

    Task<IReadOnlyList<WorkloadRevisionSnapshot>> GetWorkloadsAsync(CancellationToken ct);

    Task<IReadOnlyList<AzureResourceSnapshot>> GetAzureResourcesAsync(CancellationToken ct);

    Task<IReadOnlyList<HealthProbeSnapshot>> GetHealthProbesAsync(CancellationToken ct);

    Task<IReadOnlyList<DriftFinding>> GetDriftFindingsAsync(bool includeResolved, CancellationToken ct);
}

public interface ITwinStore : ITwinReadModel
{
    Task<bool> AppendEventIfNewAsync(ObservedEvent observedEvent, CancellationToken ct);
    Task<int> AppendEventsIfNewAsync(IEnumerable<ObservedEvent> observedEvents, CancellationToken ct);
    Task<IReadOnlyList<ObservedEvent>> AppendEventsReturnNewAsync(IEnumerable<ObservedEvent> observedEvents, CancellationToken ct);
    Task<IReadOnlySet<string>> FindExistingIdempotencyKeysAsync(IReadOnlyCollection<string> idempotencyKeys, CancellationToken ct);
    Task<int> ClearEventJournalAsync(CancellationToken ct);

    // Event-sourced read models: only the projector (applying state observation events) changes them.
    Task<IReadOnlyDictionary<string, string>> GetProjectionHashesAsync(string kind, CancellationToken ct);
    Task<int> ApplyStateObservationsAsync(IReadOnlyList<ObservedEvent> stateEvents, CancellationToken ct);
    Task ClearReadModelsAsync(CancellationToken ct);
    Task<int> CountProjectionVersionsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetReadModelFingerprintAsync(CancellationToken ct);

    // Time travel: the journal's state observations as they stood at a point in time.
    /// <summary>The newest state observation per (kind, subject) that occurred at or before <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<ObservedEvent>> GetLatestStateEventsAsync(DateTimeOffset asOf, CancellationToken ct);
    /// <summary>Every state observation of one kind that occurred at or before <paramref name="asOf"/>, oldest first.</summary>
    Task<IReadOnlyList<ObservedEvent>> GetStateEventHistoryAsync(string kind, DateTimeOffset asOf, CancellationToken ct);
    /// <summary>When the oldest state observation occurred: point-in-time state is empty before it.</summary>
    Task<DateTimeOffset?> GetStateHistoryStartAsync(CancellationToken ct);

    Task<IReadOnlyList<ObservedEvent>> QueryEventsAsync(
        DateTimeOffset? since,
        DateTimeOffset? until,
        EventSourceKind? source,
        string? pipelineId,
        string? stageName,
        string? eventType,
        int limit,
        CancellationToken ct);

    /// <summary>Journal events (not state observations) whose chain id is one of <paramref name="chainIds"/>, newest first.</summary>
    Task<IReadOnlyList<ObservedEvent>> GetEventsByChainAsync(IReadOnlyCollection<string> chainIds, DateTimeOffset? until, int limit, CancellationToken ct);

    Task UpsertCursorAsync(IngestionCursor cursor, CancellationToken ct);

    /// <summary>Space used by the twin database (SQL Server; zeros elsewhere), for keeping well under the tier cap.</summary>
    Task<DatabaseUsage> GetDatabaseUsageAsync(CancellationToken ct);
}

public sealed record DatabaseUsage(double UsedMb, double AllocatedMb, double MaxMb, IReadOnlyDictionary<string, double> LargestTablesMb);

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
