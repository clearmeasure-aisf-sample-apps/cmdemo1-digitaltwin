using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
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
    Task<IReadOnlyList<ObservedEvent>> GetEventsByChainAsync(IReadOnlyCollection<string> chainIds, DateTimeOffset? until, int limit, IReadOnlyCollection<string>? eventTypes, CancellationToken ct);

    Task UpsertCursorAsync(IngestionCursor cursor, CancellationToken ct);

    /// <summary>Space used by the twin database (SQL Server; zeros elsewhere), for keeping well under the tier cap.</summary>
    Task<DatabaseUsage> GetDatabaseUsageAsync(CancellationToken ct);

    /// <summary>UTC days (of occurrence) that hold journal rows, from <paramref name="from"/> up to, not including, <paramref name="before"/>.</summary>
    Task<IReadOnlyList<DateOnly>> GetJournalDaysAsync(DateTimeOffset from, DateTimeOffset before, CancellationToken ct);

    /// <summary>Journal rows that occurred in [<paramref name="from"/>, <paramref name="until"/>), oldest first, streamed.</summary>
    IAsyncEnumerable<ObservedEvent> StreamJournalAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>
    /// After a day is archived: removes state observations (drift excepted) superseded before the end of <paramref name="day"/>,
    /// and that day's lifecycle events <paramref name="keep"/> does not keep.
    /// </summary>
    Task<JournalCompaction> CompactJournalDayAsync(DateOnly day, JournalKeepRule keep, CancellationToken ct);

    /// <summary>Days before <paramref name="before"/> still holding rows a compaction would remove (rows that arrived after their day was archived).</summary>
    Task<IReadOnlyList<DateOnly>> GetCompactableJournalDaysAsync(DateTimeOffset before, JournalKeepRule keep, CancellationToken ct);

    /// <summary>Journal size and composition; <paramref name="detail"/> adds payload MB per kind (scans the journal).</summary>
    Task<JournalUsage> GetJournalUsageAsync(bool detail, CancellationToken ct);
}

/// <summary>
/// Where a rebuild writes: a fresh, empty copy of the journal and read models beside the live ones. Live state is
/// replaced only by <see cref="SwapAsync"/>, atomically; until then an interrupted rebuild leaves it untouched.
/// </summary>
public interface IRebuildTarget
{
    bool IsSupported { get; }
    /// <summary>Discards any previous shadow and returns a store bound to new, empty shadow tables.</summary>
    Task<ITwinStore> CreateAsync(CancellationToken ct);
    Task SwapAsync(CancellationToken ct);
    Task DiscardAsync(CancellationToken ct);
}

/// <summary>Archived journal: day files, end-of-day state checkpoints and history facts (see <see cref="JournalArchiveFormat"/>).</summary>
public interface IJournalArchive
{
    bool IsEnabled { get; }
    string Location { get; }
    Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct);
    Task<IReadOnlyList<DateOnly>> ListCheckpointsAsync(CancellationToken ct);
    IAsyncEnumerable<ObservedEvent> ReadDayAsync(DateOnly day, CancellationToken ct);
    IAsyncEnumerable<ObservedEvent> ReadCheckpointAsync(DateOnly day, CancellationToken ct);
    Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct);
    Task<int> WriteCheckpointAsync(DateOnly day, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct);
    Task<JournalArchiveMeta?> ReadMetaAsync(CancellationToken ct);
    Task WriteMetaAsync(JournalArchiveMeta meta, CancellationToken ct);
}

/// <summary>
/// Lifecycle events that stay in SQL after their day is archived, because change chains, DORA and config views read them:
/// <paramref name="Always"/> with a chain id, <paramref name="FailuresOnly"/> with a chain id when they failed, and every <paramref name="Unconditional"/>.
/// </summary>
public sealed record JournalKeepRule(IReadOnlyList<string> Always, IReadOnlyList<string> FailuresOnly, IReadOnlyList<string> Unconditional)
{
    public bool Keeps(ObservedEvent e)
        => (e.ChainId is not null && (Always.Contains(e.EventType) || (FailuresOnly.Contains(e.EventType) && e.Outcome == EventOutcome.Failure)))
           || Unconditional.Contains(e.EventType);
}

/// <summary>What one compaction removed from SQL.</summary>
public sealed record JournalCompaction(int SupersededStateRows, int LifecycleRows);

public sealed record DatabaseUsage(double UsedMb, double AllocatedMb, double MaxMb, IReadOnlyDictionary<string, double> LargestTablesMb);

/// <param name="TableMb">The journal table with its indexes.</param>
/// <param name="ModelTablesMb">Every table a rebuild recreates beside the live ones (journal, read models, projection versions).</param>
public sealed record JournalUsage(long Rows, long StateRows, long DriftRows, long LifecycleRows, double TableMb, double ModelTablesMb, IReadOnlyList<JournalKindUsage>? Kinds = null);

/// <param name="Kind">State kind ("state:work-item") or lifecycle source ("lifecycle:GitHub").</param>
public sealed record JournalKindUsage(string Kind, long Rows, double PayloadMb);

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
