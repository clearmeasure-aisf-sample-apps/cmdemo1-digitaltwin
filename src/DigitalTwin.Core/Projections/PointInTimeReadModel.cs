using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Core.Projections;

/// <summary>
/// The twin's read models as they stood at <see cref="AsOf"/>, folded in memory from state observation events with the
/// same latest-wins and replace-set semantics as the materialized projection, so "as of now" equals the live tables.
/// </summary>
public sealed class PointInTimeReadModel : ITwinReadModel
{
    private readonly Dictionary<StateKindId, List<object>> _rows = [];
    private readonly List<DriftFinding> _drift = [];

    private PointInTimeReadModel(DateTimeOffset asOf) => AsOf = asOf;

    public DateTimeOffset AsOf { get; }

    public int StateEventCount { get; private init; }

    /// <param name="latestStateEvents">Newest state observation per (kind, subject) at or before <paramref name="asOf"/>.</param>
    /// <param name="driftHistory">Every drift set observation at or before <paramref name="asOf"/> (drift keeps detection history).</param>
    public static PointInTimeReadModel Build(DateTimeOffset asOf, IEnumerable<ObservedEvent> latestStateEvents, IEnumerable<ObservedEvent> driftHistory)
    {
        var latest = new Dictionary<(string Kind, string Subject), ObservedEvent>();
        foreach (var ev in latestStateEvents.Where(e => StateObservations.IsStateEvent(e) && e.OccurredAt <= asOf))
        {
            var key = (ev.SubjectType, ev.SubjectId.ToUpperInvariant());
            if (!latest.TryGetValue(key, out var current) || StateObservations.IsNewer(ev.OccurredAt, ev.IdempotencyKey, current.OccurredAt, current.IdempotencyKey))
            {
                latest[key] = ev;
            }
        }

        var model = new PointInTimeReadModel(asOf) { StateEventCount = latest.Count };
        foreach (var ev in latest.Values)
        {
            var kind = StateKinds.Find(ev.SubjectType);
            if (kind is null || kind.Id == StateKindId.Drift)
            {
                continue;
            }

            var rows = model.RowsOf(kind.Id);
            if (kind.Mode == StateMode.Upsert)
            {
                rows.Add(StateObservations.DeserializeEntity(kind, ev.PayloadJson));
            }
            else
            {
                rows.AddRange(StateObservations.DeserializeSet(kind, ev.PayloadJson));
            }
        }

        foreach (var kind in StateKinds.All.Where(k => k.Mode == StateMode.ReplaceSet && k.Id != StateKindId.Drift))
        {
            var deduped = model.RowsOf(kind.Id).GroupBy(kind.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
            model._rows[kind.Id] = deduped;
        }

        model.FoldDrift(driftHistory.Where(e => StateObservations.IsStateEvent(e) && e.SubjectType == StateKinds.Drift.Name && e.OccurredAt <= asOf));
        return model;
    }

    /// <summary>Mirrors the materialized drift reconciliation: re-detection resets DetectedAt, disappearance resolves.</summary>
    private void FoldDrift(IEnumerable<ObservedEvent> history)
    {
        var findings = new Dictionary<string, DriftFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in history.OrderBy(e => e.OccurredAt.UtcTicks).ThenBy(e => e.IdempotencyKey, StringComparer.Ordinal))
        {
            var evaluated = StateObservations.DeserializeSet(StateKinds.Drift, ev.PayloadJson).Cast<DriftFinding>().ToList();
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in evaluated)
            {
                activeKeys.Add(item.DriftKey);
                if (findings.TryGetValue(item.DriftKey, out var current))
                {
                    current.Category = item.Category;
                    current.Severity = item.Severity;
                    current.StageName = item.StageName;
                    current.DeployableName = item.DeployableName;
                    current.ResourceId = item.ResourceId;
                    current.ExpectedValue = item.ExpectedValue;
                    current.ObservedValue = item.ObservedValue;
                    current.Summary = item.Summary;
                    current.RemediationHint = item.RemediationHint;
                    if (!current.IsActive)
                    {
                        current.DetectedAt = ev.OccurredAt;
                    }
                    current.IsActive = true;
                    current.ResolvedAt = null;
                }
                else
                {
                    findings[item.DriftKey] = item;
                }
            }

            foreach (var current in findings.Values.Where(d => d.IsActive && !activeKeys.Contains(d.DriftKey)))
            {
                current.IsActive = false;
                current.ResolvedAt = ev.OccurredAt;
            }
        }
        _drift.AddRange(findings.Values);
    }

    private List<object> RowsOf(StateKindId id)
    {
        if (!_rows.TryGetValue(id, out var rows))
        {
            rows = [];
            _rows[id] = rows;
        }
        return rows;
    }

    private IEnumerable<T> All<T>(StateKindId id) => RowsOf(id).Cast<T>();

    private static Task<IReadOnlyList<T>> Result<T>(IEnumerable<T> rows) => Task.FromResult<IReadOnlyList<T>>(rows.ToList());

    // Ingestion cursors are operational bookkeeping, not observed state, so they have no history.
    public Task<IReadOnlyList<IngestionCursor>> GetCursorsAsync(CancellationToken ct) => Result<IngestionCursor>([]);

    public Task<IReadOnlyList<WebhookRegistrationStatus>> GetWebhookStatusesAsync(CancellationToken ct)
        => Result(All<WebhookRegistrationStatus>(StateKindId.WebhookStatus).OrderBy(w => w.Source, StringComparer.Ordinal).ThenBy(w => w.Target, StringComparer.Ordinal));

    public Task<IReadOnlyList<RepositorySnapshot>> GetRepositoriesAsync(CancellationToken ct)
        => Result(All<RepositorySnapshot>(StateKindId.Repository).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<SourceChangeSnapshot>> GetSourceChangesAsync(CancellationToken ct)
        => Result(All<SourceChangeSnapshot>(StateKindId.SourceChange).OrderByDescending(c => c.UpdatedAt));

    public Task<IReadOnlyList<PipelineRunSnapshot>> GetPipelineRunsAsync(int limit, CancellationToken ct)
        => Result(All<PipelineRunSnapshot>(StateKindId.PipelineRun).OrderByDescending(r => r.UpdatedAt).Take(limit));

    public Task<IReadOnlyList<ArtifactPackageSnapshot>> GetArtifactsAsync(CancellationToken ct)
        => Result(All<ArtifactPackageSnapshot>(StateKindId.Artifact).OrderByDescending(a => a.PublishedAt));

    public Task<IReadOnlyList<WorkItemSnapshot>> GetWorkItemsAsync(CancellationToken ct)
        => Result(All<WorkItemSnapshot>(StateKindId.WorkItem).OrderByDescending(w => w.UpdatedAt));

    public Task<IReadOnlyList<ReleaseSnapshot>> GetReleasesAsync(int limit, CancellationToken ct)
        => Result(All<ReleaseSnapshot>(StateKindId.Release).OrderByDescending(r => r.CreatedAt).Take(limit));

    public Task<IReadOnlyList<DeliveryExecutionSnapshot>> GetDeliveriesAsync(int limit, CancellationToken ct)
        => Result(All<DeliveryExecutionSnapshot>(StateKindId.Delivery).OrderByDescending(d => d.QueuedAt).Take(limit));

    public Task<IReadOnlyList<RunbookExecutionSnapshot>> GetRunbooksAsync(int limit, CancellationToken ct)
        => Result(All<RunbookExecutionSnapshot>(StateKindId.Runbook).OrderByDescending(r => r.CreatedAt).Take(limit));

    public Task<IReadOnlyList<DeploymentFreezeSnapshot>> GetFreezesAsync(CancellationToken ct)
        => Result(All<DeploymentFreezeSnapshot>(StateKindId.Freeze).OrderBy(f => f.FreezeId, StringComparer.OrdinalIgnoreCase));

    public Task<DesiredSystemConfigSnapshot?> GetDesiredConfigAsync(CancellationToken ct)
        => Task.FromResult(All<DesiredSystemConfigSnapshot>(StateKindId.DesiredConfig).FirstOrDefault(c => c.Id == "cmdemo1"));

    public Task<IReadOnlyList<EnvironmentInstanceSnapshot>> GetEnvironmentInstancesAsync(CancellationToken ct)
        => Result(All<EnvironmentInstanceSnapshot>(StateKindId.Environment).OrderBy(e => e.StageName, StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<WorkloadRevisionSnapshot>> GetWorkloadsAsync(CancellationToken ct)
        => Result(All<WorkloadRevisionSnapshot>(StateKindId.Workload).OrderBy(w => w.Id, StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<AzureResourceSnapshot>> GetAzureResourcesAsync(CancellationToken ct)
        => Result(All<AzureResourceSnapshot>(StateKindId.AzureResource).OrderBy(r => r.ResourceGroup, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<HealthProbeSnapshot>> GetHealthProbesAsync(CancellationToken ct)
        => Result(All<HealthProbeSnapshot>(StateKindId.HealthProbe).OrderBy(p => p.TargetKey, StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<DriftFinding>> GetDriftFindingsAsync(bool includeResolved, CancellationToken ct)
        => Result(_drift.Where(d => includeResolved || d.IsActive).OrderByDescending(d => d.Severity).ThenByDescending(d => d.DetectedAt));
}
