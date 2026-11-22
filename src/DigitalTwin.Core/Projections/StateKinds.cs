using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Core.Projections;

public enum StateKindId
{
    Repository,
    SourceChange,
    PipelineRun,
    Artifact,
    WorkItem,
    Release,
    Delivery,
    Runbook,
    Freeze,
    DesiredConfig,
    Environment,
    Workload,
    AzureResource,
    HealthProbe,
    Drift,
    WebhookStatus
}

public enum StateMode
{
    /// <summary>One event per entity; the latest observation of each key wins.</summary>
    Upsert,

    /// <summary>One event per scope carrying the complete set; the latest set replaces the scope.</summary>
    ReplaceSet
}

/// <summary>Describes one current-state read model that is projected purely from state observation events.</summary>
public sealed record StateKind(
    StateKindId Id,
    string Name,
    Type ClrType,
    StateMode Mode,
    Func<object, string> Key,
    IReadOnlySet<string> VolatileProperties);

/// <summary>Catalog of every event-sourced read model. Volatile properties are excluded from change detection.</summary>
public static class StateKinds
{
    public const string EventType = "twin.state.observed";

    public static readonly StateKind Repository = Upsert<RepositorySnapshot>(StateKindId.Repository, "repository", r => r.Name, nameof(RepositorySnapshot.UpdatedAt));
    public static readonly StateKind SourceChange = Upsert<SourceChangeSnapshot>(StateKindId.SourceChange, "source-change", c => c.Id);
    public static readonly StateKind PipelineRun = Upsert<PipelineRunSnapshot>(StateKindId.PipelineRun, "pipeline-run", r => r.Id);
    public static readonly StateKind Artifact = Upsert<ArtifactPackageSnapshot>(StateKindId.Artifact, "artifact", a => a.Id);
    public static readonly StateKind WorkItem = Upsert<WorkItemSnapshot>(StateKindId.WorkItem, "work-item", w => w.Id);
    public static readonly StateKind Release = Upsert<ReleaseSnapshot>(StateKindId.Release, "release", r => r.ReleaseId);
    public static readonly StateKind Delivery = Upsert<DeliveryExecutionSnapshot>(StateKindId.Delivery, "delivery", d => d.DeploymentId);
    public static readonly StateKind Runbook = Upsert<RunbookExecutionSnapshot>(StateKindId.Runbook, "runbook", r => r.TaskId);
    public static readonly StateKind Freeze = Set<DeploymentFreezeSnapshot>(StateKindId.Freeze, "freeze", f => f.FreezeId, nameof(DeploymentFreezeSnapshot.UpdatedAt));
    public static readonly StateKind DesiredConfig = Upsert<DesiredSystemConfigSnapshot>(StateKindId.DesiredConfig, "desired-config", c => c.Id, nameof(DesiredSystemConfigSnapshot.UpdatedAt));
    public static readonly StateKind Environment = Upsert<EnvironmentInstanceSnapshot>(StateKindId.Environment, "environment", e => e.StageName, nameof(EnvironmentInstanceSnapshot.LastEvaluatedAt));
    public static readonly StateKind Workload = Set<WorkloadRevisionSnapshot>(StateKindId.Workload, "workload", w => w.Id, nameof(WorkloadRevisionSnapshot.ObservedAt));
    public static readonly StateKind AzureResource = Set<AzureResourceSnapshot>(StateKindId.AzureResource, "azure-resource", r => r.ResourceId, nameof(AzureResourceSnapshot.ObservedAt));
    public static readonly StateKind HealthProbe = Upsert<HealthProbeSnapshot>(StateKindId.HealthProbe, "health-probe", p => p.TargetKey, nameof(HealthProbeSnapshot.CheckedAt), nameof(HealthProbeSnapshot.LatencyMs));
    public static readonly StateKind Drift = Set<DriftFinding>(StateKindId.Drift, "drift", d => d.DriftKey, nameof(DriftFinding.DetectedAt), nameof(DriftFinding.ResolvedAt), nameof(DriftFinding.IsActive));
    public static readonly StateKind WebhookStatus = Set<WebhookRegistrationStatus>(StateKindId.WebhookStatus, "webhook-status", w => w.Id, nameof(WebhookRegistrationStatus.CheckedAt));

    public static IReadOnlyList<StateKind> All { get; } =
    [
        Repository, SourceChange, PipelineRun, Artifact, WorkItem, Release, Delivery, Runbook,
        Freeze, DesiredConfig, Environment, Workload, AzureResource, HealthProbe, Drift, WebhookStatus
    ];

    public static StateKind? Find(string name) => All.FirstOrDefault(k => k.Name == name);

    private static StateKind Upsert<T>(StateKindId id, string name, Func<T, string> key, params string[] volatileProperties)
        => new(id, name, typeof(T), StateMode.Upsert, o => key((T)o), new HashSet<string>(volatileProperties, StringComparer.Ordinal));

    private static StateKind Set<T>(StateKindId id, string name, Func<T, string> key, params string[] volatileProperties)
        => new(id, name, typeof(T), StateMode.ReplaceSet, o => key((T)o), new HashSet<string>(volatileProperties, StringComparer.Ordinal));
}

/// <summary>Encodes and decodes state observation events (the only way read models change).</summary>
public static class StateObservations
{
    public static ObservedEvent ToEvent(StateKind kind, string subject, object payload, string contentHash, EventSourceKind source, DateTimeOffset observedAt)
        => new()
        {
            EventId = $"state-{kind.Name}-{observedAt.UtcTicks}-{contentHash}",
            IdempotencyKey = $"state:{kind.Name}:{observedAt.UtcTicks:D19}:{contentHash}:{subject}",
            Source = source,
            SourceEventId = $"{kind.Name}:{subject}:{observedAt.UtcTicks}",
            EventType = StateKinds.EventType,
            SubjectType = kind.Name,
            SubjectId = subject,
            Predicate = kind.Mode == StateMode.Upsert ? "upsert" : "replace-set",
            OccurredAt = observedAt,
            IngestedAt = observedAt,
            Channel = IngestionChannel.Poll,
            Actor = "twin-state-observer",
            Summary = $"{kind.Name} {subject} observed",
            PayloadJson = JsonSerializer.Serialize(payload, kind.Mode == StateMode.Upsert ? kind.ClrType : typeof(List<>).MakeGenericType(kind.ClrType), IntakeJson.Options)
        };

    public static bool IsStateEvent(ObservedEvent e) => e.EventType == StateKinds.EventType;

    /// <summary>Content hash over non-volatile properties; equal hashes mean nothing material changed.</summary>
    public static string ContentHash(StateKind kind, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), IntakeJson.Options);
        Strip(node, kind.VolatileProperties);
        var canonical = node?.ToJsonString() ?? "null";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    public static string SetHash(StateKind kind, IEnumerable<object> items)
        => ContentHash(kind, items.OrderBy(kind.Key, StringComparer.Ordinal).Select(i => (object)ContentHash(kind, i)).ToList());

    public static object DeserializeEntity(StateKind kind, string payloadJson)
        => JsonSerializer.Deserialize(payloadJson, kind.ClrType, IntakeJson.Options)
           ?? throw new InvalidOperationException($"Empty {kind.Name} state payload.");

    public static IReadOnlyList<object> DeserializeSet(StateKind kind, string payloadJson)
        => ((System.Collections.IEnumerable?)JsonSerializer.Deserialize(payloadJson, typeof(List<>).MakeGenericType(kind.ClrType), IntakeJson.Options))
            ?.Cast<object>().ToList() ?? [];

    public static string HashOf(ObservedEvent stateEvent)
    {
        var parts = stateEvent.IdempotencyKey.Split(':', 5);
        return parts.Length >= 4 ? parts[3] : string.Empty;
    }

    /// <summary>Latest-wins ordering shared by the materialized projection and point-in-time queries.</summary>
    public static bool IsNewer(DateTimeOffset observedAt, string eventKey, DateTimeOffset currentObservedAt, string currentEventKey)
    {
        var byTime = observedAt.UtcTicks.CompareTo(currentObservedAt.UtcTicks);
        return byTime != 0 ? byTime > 0 : string.CompareOrdinal(eventKey, currentEventKey) > 0;
    }

    private static void Strip(JsonNode? node, IReadOnlySet<string> volatileProperties)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (volatileProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        obj.Remove(name);
                    }
                    else
                    {
                        Strip(obj[name], volatileProperties);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Strip(item, volatileProperties);
                }
                break;
        }
    }
}
