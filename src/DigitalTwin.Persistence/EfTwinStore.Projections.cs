using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;
using Microsoft.EntityFrameworkCore;

namespace DigitalTwin.Persistence;

/// <summary>Materialized projections: read models change only by applying state observation events.</summary>
public sealed partial class EfTwinStore
{
    public async Task<IReadOnlyDictionary<string, string>> GetProjectionHashesAsync(string kind, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ProjectionVersions.AsNoTracking()
            .Where(v => v.Kind == kind)
            .ToDictionaryAsync(v => v.Key, v => v.ContentHash, StringComparer.Ordinal, ct);
    }

    public async Task<int> CountProjectionVersionsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ProjectionVersions.CountAsync(ct);
    }

    public async Task<int> ApplyStateObservationsAsync(IReadOnlyList<ObservedEvent> stateEvents, CancellationToken ct)
    {
        var applied = 0;
        var limits = await GetObservedEventColumnLimitsAsync(ct);
        var ordered = stateEvents
            .Where(StateObservations.IsStateEvent)
            .Select(e => FitToColumns(e, limits))
            .OrderBy(e => e.OccurredAt.UtcTicks)
            .ThenBy(e => e.IdempotencyKey, StringComparer.Ordinal);

        foreach (var ev in ordered)
        {
            var kind = StateKinds.Find(ev.SubjectType);
            if (kind is null)
            {
                continue;
            }

            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var current = await db.ProjectionVersions.AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Kind == kind.Name && v.Key == ev.SubjectId, ct);
                if (current is not null && !StateObservations.IsNewer(ev.OccurredAt, ev.IdempotencyKey, current.ObservedAt, current.EventKey))
                {
                    continue;
                }
            }

            await ProjectAsync(kind, ev, ct);

            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var version = await db.ProjectionVersions.FirstOrDefaultAsync(v => v.Kind == kind.Name && v.Key == ev.SubjectId, ct);
                if (version is null)
                {
                    version = new ProjectionVersion { Kind = kind.Name, Key = ev.SubjectId };
                    db.ProjectionVersions.Add(version);
                }
                version.ObservedAt = ev.OccurredAt;
                version.EventKey = ev.IdempotencyKey;
                version.ContentHash = StateObservations.HashOf(ev);
                await db.SaveChangesAsync(ct);
            }
            applied++;
        }
        return applied;
    }

    private Task ProjectAsync(StateKind kind, ObservedEvent ev, CancellationToken ct)
    {
        T One<T>() => (T)StateObservations.DeserializeEntity(kind, ev.PayloadJson);
        List<T> Set<T>() => StateObservations.DeserializeSet(kind, ev.PayloadJson).Cast<T>().ToList();

        return kind.Id switch
        {
            StateKindId.Repository => UpsertRepositoriesAsync([One<RepositorySnapshot>()], ct),
            StateKindId.SourceChange => UpsertSourceChangesAsync([One<SourceChangeSnapshot>()], ct),
            StateKindId.PipelineRun => UpsertPipelineRunsAsync([One<PipelineRunSnapshot>()], ct),
            StateKindId.Artifact => UpsertArtifactsAsync([One<ArtifactPackageSnapshot>()], ct),
            StateKindId.WorkItem => UpsertWorkItemsAsync([One<WorkItemSnapshot>()], ct),
            StateKindId.Release => UpsertReleasesAsync([One<ReleaseSnapshot>()], ct),
            StateKindId.Delivery => UpsertDeliveriesAsync([One<DeliveryExecutionSnapshot>()], ct),
            StateKindId.Runbook => UpsertRunbooksAsync([One<RunbookExecutionSnapshot>()], ct),
            StateKindId.Freeze => ReplaceFreezesAsync(Set<DeploymentFreezeSnapshot>(), ct),
            StateKindId.DesiredConfig => UpsertDesiredConfigAsync(One<DesiredSystemConfigSnapshot>(), ct),
            StateKindId.Environment => UpsertEnvironmentInstancesAsync([One<EnvironmentInstanceSnapshot>()], ct),
            StateKindId.Workload => ReplaceWorkloadsAsync(Set<WorkloadRevisionSnapshot>(), ct),
            StateKindId.AzureResource => ReplaceAzureResourcesAsync(Set<AzureResourceSnapshot>(), ct),
            StateKindId.HealthProbe => UpsertHealthProbesAsync([One<HealthProbeSnapshot>()], ct),
            StateKindId.Drift => ReconcileDriftFindingsAsync(Set<DriftFinding>(), ev.OccurredAt, ct),
            StateKindId.WebhookStatus => ReplaceWebhookStatusesAsync(ev.SubjectId, Set<WebhookRegistrationStatus>(), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind.Id, "Unhandled state kind")
        };
    }

    public async Task ClearReadModelsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Repositories.ExecuteDeleteAsync(ct);
        await db.SourceChanges.ExecuteDeleteAsync(ct);
        await db.PipelineRuns.ExecuteDeleteAsync(ct);
        await db.Artifacts.ExecuteDeleteAsync(ct);
        await db.WorkItems.ExecuteDeleteAsync(ct);
        await db.Releases.ExecuteDeleteAsync(ct);
        await db.Deliveries.ExecuteDeleteAsync(ct);
        await db.RunbookExecutions.ExecuteDeleteAsync(ct);
        await db.DeploymentFreezes.ExecuteDeleteAsync(ct);
        await db.DesiredSystemConfigs.ExecuteDeleteAsync(ct);
        await db.EnvironmentInstances.ExecuteDeleteAsync(ct);
        await db.WorkloadRevisions.ExecuteDeleteAsync(ct);
        await db.AzureResources.ExecuteDeleteAsync(ct);
        await db.HealthProbes.ExecuteDeleteAsync(ct);
        await db.DriftFindings.ExecuteDeleteAsync(ct);
        await db.WebhookRegistrationStatuses.ExecuteDeleteAsync(ct);
        await db.ProjectionVersions.ExecuteDeleteAsync(ct);
    }

    /// <summary>Per read model "count:hash" over every row (journal excludes its surrogate Id).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetReadModelFingerprintAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [StateKinds.Repository.Name] = await Fingerprint(db.Repositories, r => r.Name, ct),
            [StateKinds.SourceChange.Name] = await Fingerprint(db.SourceChanges, r => r.Id, ct),
            [StateKinds.PipelineRun.Name] = await Fingerprint(db.PipelineRuns, r => r.Id, ct),
            [StateKinds.Artifact.Name] = await Fingerprint(db.Artifacts, r => r.Id, ct),
            [StateKinds.WorkItem.Name] = await Fingerprint(db.WorkItems, r => r.Id, ct),
            [StateKinds.Release.Name] = await Fingerprint(db.Releases, r => r.ReleaseId, ct),
            [StateKinds.Delivery.Name] = await Fingerprint(db.Deliveries, r => r.DeploymentId, ct),
            [StateKinds.Runbook.Name] = await Fingerprint(db.RunbookExecutions, r => r.TaskId, ct),
            [StateKinds.Freeze.Name] = await Fingerprint(db.DeploymentFreezes, r => r.FreezeId, ct),
            [StateKinds.DesiredConfig.Name] = await Fingerprint(db.DesiredSystemConfigs, r => r.Id, ct),
            [StateKinds.Environment.Name] = await Fingerprint(db.EnvironmentInstances, r => r.StageName, ct),
            [StateKinds.Workload.Name] = await Fingerprint(db.WorkloadRevisions, r => r.Id, ct),
            [StateKinds.AzureResource.Name] = await Fingerprint(db.AzureResources, r => r.ResourceId, ct),
            [StateKinds.HealthProbe.Name] = await Fingerprint(db.HealthProbes, r => r.TargetKey, ct),
            [StateKinds.Drift.Name] = await Fingerprint(db.DriftFindings, r => r.DriftKey, ct),
            [StateKinds.WebhookStatus.Name] = await Fingerprint(db.WebhookRegistrationStatuses, r => r.Id, ct),
            ["projection-version"] = await Fingerprint(db.ProjectionVersions, r => $"{r.Kind}|{r.Key}", ct)
        };

        // Journal: streamed in key order so the (large) payload column is never held in memory at once.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        await foreach (var e in db.ObservedEvents.AsNoTracking().OrderBy(e => e.IdempotencyKey).AsAsyncEnumerable().WithCancellation(ct))
        {
            e.Id = 0;
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e, IntakeJson.Options)));
            count++;
        }
        result["journal"] = $"{count}:{Convert.ToHexStringLower(hash.GetHashAndReset())[..16]}";
        return result;
    }

    private static async Task<string> Fingerprint<T>(IQueryable<T> rows, Func<T, string> key, CancellationToken ct) where T : class
    {
        var list = await rows.AsNoTracking().ToListAsync(ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in list.OrderBy(key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row, IntakeJson.Options)));
        }
        return $"{list.Count}:{Convert.ToHexStringLower(hash.GetHashAndReset())[..16]}";
    }
}
