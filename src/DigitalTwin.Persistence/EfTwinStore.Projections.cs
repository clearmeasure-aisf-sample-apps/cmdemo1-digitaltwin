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

    /// <summary>
    /// Applies a batch with the same result as applying each event in (time, key) order, but with a constant number of
    /// round trips: upserts and replace-sets only need the newest applicable event per entity; drift needs every one
    /// (it keeps detection history).
    /// </summary>
    public async Task<int> ApplyStateObservationsAsync(IReadOnlyList<ObservedEvent> stateEvents, CancellationToken ct)
    {
        var limits = await GetObservedEventColumnLimitsAsync(ct);
        var ordered = stateEvents
            .Where(e => StateObservations.IsStateEvent(e) && StateKinds.Find(e.SubjectType) is not null)
            .Select(e => FitToColumns(e, limits))
            .OrderBy(e => e.OccurredAt.UtcTicks)
            .ThenBy(e => e.IdempotencyKey, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var versions = await LoadVersionsAsync(ordered, ct);
        var newest = new Dictionary<VersionKey, ObservedEvent>();
        var driftSequence = new List<ObservedEvent>();
        var applied = 0;
        foreach (var ev in ordered)
        {
            var key = new VersionKey(ev.SubjectType, ev.SubjectId);
            if (versions.TryGetValue(key, out var current) && !StateObservations.IsNewer(ev.OccurredAt, ev.IdempotencyKey, current.ObservedAt, current.EventKey))
            {
                continue;
            }
            versions[key] = (ev.OccurredAt, ev.IdempotencyKey);
            newest[key] = ev;
            if (ev.SubjectType == StateKinds.Drift.Name)
            {
                driftSequence.Add(ev);
            }
            applied++;
        }
        if (applied == 0)
        {
            return 0;
        }

        foreach (var group in newest.Values.Where(e => e.SubjectType != StateKinds.Drift.Name).GroupBy(e => e.SubjectType, StringComparer.Ordinal))
        {
            await ProjectAsync(StateKinds.Find(group.Key)!, group.OrderBy(e => e.OccurredAt.UtcTicks).ThenBy(e => e.IdempotencyKey, StringComparer.Ordinal).ToList(), ct);
        }
        foreach (var ev in driftSequence)
        {
            await ProjectAsync(StateKinds.Drift, [ev], ct);
        }

        await SaveVersionsAsync(newest.Values, ct);
        return applied;
    }

    private readonly record struct VersionKey(string Kind, string Key)
    {
        public bool Equals(VersionKey other)
            => string.Equals(Kind, other.Kind, StringComparison.Ordinal) && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Key));
    }

    private async Task<Dictionary<VersionKey, (DateTimeOffset ObservedAt, string EventKey)>> LoadVersionsAsync(IReadOnlyList<ObservedEvent> events, CancellationToken ct)
    {
        var result = new Dictionary<VersionKey, (DateTimeOffset, string)>();
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var byKind in events.GroupBy(e => e.SubjectType, StringComparer.Ordinal))
        {
            foreach (var chunk in byKind.Select(e => e.SubjectId).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(500))
            {
                var rows = await db.ProjectionVersions.AsNoTracking()
                    .Where(v => v.Kind == byKind.Key && chunk.Contains(v.Key))
                    .ToListAsync(ct);
                foreach (var row in rows)
                {
                    result[new VersionKey(row.Kind, row.Key)] = (row.ObservedAt, row.EventKey);
                }
            }
        }
        return result;
    }

    private async Task SaveVersionsAsync(IEnumerable<ObservedEvent> newestPerKey, CancellationToken ct)
    {
        var events = newestPerKey.ToList();
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var byKind in events.GroupBy(e => e.SubjectType, StringComparer.Ordinal))
        {
            foreach (var chunk in byKind.Chunk(500))
            {
                var keys = chunk.Select(e => e.SubjectId).ToList();
                var tracked = (await db.ProjectionVersions.Where(v => v.Kind == byKind.Key && keys.Contains(v.Key)).ToListAsync(ct))
                    .ToDictionary(v => v.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var ev in chunk)
                {
                    if (!tracked.TryGetValue(ev.SubjectId, out var version))
                    {
                        version = new ProjectionVersion { Kind = byKind.Key, Key = ev.SubjectId };
                        db.ProjectionVersions.Add(version);
                        tracked[ev.SubjectId] = version;
                    }
                    version.ObservedAt = ev.OccurredAt;
                    version.EventKey = ev.IdempotencyKey;
                    version.ContentHash = StateObservations.HashOf(ev);
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Projects events of one kind, oldest first; for upsert kinds there is at most one event per entity.</summary>
    private async Task ProjectAsync(StateKind kind, IReadOnlyList<ObservedEvent> events, CancellationToken ct)
    {
        List<T> Many<T>() => events.Select(e => (T)StateObservations.DeserializeEntity(kind, e.PayloadJson)).ToList();
        List<T> Set<T>(ObservedEvent e) => StateObservations.DeserializeSet(kind, e.PayloadJson).Cast<T>().ToList();

        switch (kind.Id)
        {
            case StateKindId.Repository: await UpsertRepositoriesAsync(Many<RepositorySnapshot>(), ct); break;
            case StateKindId.SourceChange: await UpsertSourceChangesAsync(Many<SourceChangeSnapshot>(), ct); break;
            case StateKindId.PipelineRun: await UpsertPipelineRunsAsync(Many<PipelineRunSnapshot>(), ct); break;
            case StateKindId.Artifact: await UpsertArtifactsAsync(Many<ArtifactPackageSnapshot>(), ct); break;
            case StateKindId.WorkItem: await UpsertWorkItemsAsync(Many<WorkItemSnapshot>(), ct); break;
            case StateKindId.Release: await UpsertReleasesAsync(Many<ReleaseSnapshot>(), ct); break;
            case StateKindId.Delivery: await UpsertDeliveriesAsync(Many<DeliveryExecutionSnapshot>(), ct); break;
            case StateKindId.Runbook: await UpsertRunbooksAsync(Many<RunbookExecutionSnapshot>(), ct); break;
            case StateKindId.Environment: await UpsertEnvironmentInstancesAsync(Many<EnvironmentInstanceSnapshot>(), ct); break;
            case StateKindId.HealthProbe: await UpsertHealthProbesAsync(Many<HealthProbeSnapshot>(), ct); break;
            case StateKindId.DesiredConfig:
                foreach (var config in Many<DesiredSystemConfigSnapshot>())
                {
                    await UpsertDesiredConfigAsync(config, ct);
                }
                break;
            case StateKindId.Freeze:
                foreach (var e in events) { await ReplaceFreezesAsync(Set<DeploymentFreezeSnapshot>(e), ct); }
                break;
            case StateKindId.Workload:
                foreach (var e in events) { await ReplaceWorkloadsAsync(Set<WorkloadRevisionSnapshot>(e), ct); }
                break;
            case StateKindId.AzureResource:
                foreach (var e in events) { await ReplaceAzureResourcesAsync(Set<AzureResourceSnapshot>(e), ct); }
                break;
            case StateKindId.WebhookStatus:
                foreach (var e in events) { await ReplaceWebhookStatusesAsync(e.SubjectId, Set<WebhookRegistrationStatus>(e), ct); }
                break;
            case StateKindId.Drift:
                foreach (var e in events) { await ReconcileDriftFindingsAsync(Set<DriftFinding>(e), e.OccurredAt, ct); }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind.Id, "Unhandled state kind");
        }
    }

    public async Task ClearReadModelsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await ClearAsync(db, db.Repositories, ct);
        await ClearAsync(db, db.SourceChanges, ct);
        await ClearAsync(db, db.PipelineRuns, ct);
        await ClearAsync(db, db.Artifacts, ct);
        await ClearAsync(db, db.WorkItems, ct);
        await ClearAsync(db, db.Releases, ct);
        await ClearAsync(db, db.Deliveries, ct);
        await ClearAsync(db, db.RunbookExecutions, ct);
        await ClearAsync(db, db.DeploymentFreezes, ct);
        await ClearAsync(db, db.DesiredSystemConfigs, ct);
        await ClearAsync(db, db.EnvironmentInstances, ct);
        await ClearAsync(db, db.WorkloadRevisions, ct);
        await ClearAsync(db, db.AzureResources, ct);
        await ClearAsync(db, db.HealthProbes, ct);
        await ClearAsync(db, db.DriftFindings, ct);
        await ClearAsync(db, db.WebhookRegistrationStatuses, ct);
        await ClearAsync(db, db.ProjectionVersions, ct);
    }

    /// <summary>TRUNCATE on SQL Server (minimally logged; a fully logged DELETE of the journal crawls on small tiers).</summary>
    private static async Task ClearAsync<T>(TwinDbContext db, DbSet<T> rows, CancellationToken ct) where T : class
    {
        if (!db.Database.IsSqlServer())
        {
            await rows.ExecuteDeleteAsync(ct);
            return;
        }
        // Table names come from the EF model, never from input.
        var entity = db.Model.FindEntityType(typeof(T))!;
        var sql = "TRUNCATE TABLE [" + (entity.GetSchema() ?? "dbo") + "].[" + entity.GetTableName() + "]";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>Per read model "count:hash" over every row (journal excludes its surrogate Id).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetReadModelFingerprintAsync(CancellationToken ct, bool includeJournal = true)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
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

        if (includeJournal)
        {
            result["journal"] = db.Database.IsSqlServer() ? await JournalFingerprintSqlServerAsync(db, null, ct) : await JournalFingerprintStreamedAsync(db, null, ct);
        }
        return result;
    }

    public async Task<string> GetJournalFingerprintAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return db.Database.IsSqlServer() ? await JournalFingerprintSqlServerAsync(db, before, ct) : await JournalFingerprintStreamedAsync(db, before, ct);
    }

    /// <summary>
    /// Computed in the database from two narrow covering indexes, never the journal's data pages: scanning a few hundred MB
    /// of payloads takes minutes on a 5 DTU tier, and this runs while live intake is paused for a rebuild swap. State
    /// observation keys embed their content hash; type, subject and time cover the rest. Read models are fingerprinted
    /// in full separately. BINARY_CHECKSUM is case-sensitive; CHECKSUM_AGG is order-independent.
    /// </summary>
    private static async Task<string> JournalFingerprintSqlServerAsync(TwinDbContext db, DateTimeOffset? before, CancellationToken ct)
    {
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var table = JournalTable(db);
        var where = before is null ? "" : " WHERE OccurredAt < {0}";
        object[] parameters = before is null ? [] : [before.Value];
        var keysSql = "SELECT COUNT_BIG(*) AS [Count], ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(IdempotencyKey)), 0) AS [Checksum] FROM " + table + where;
        var shapeSql = "SELECT COUNT_BIG(*) AS [Count], ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(EventType, SubjectType, SubjectId, OccurredAt)), 0) AS [Checksum] FROM " + table + where;
        var keys = await db.Database.SqlQueryRaw<JournalChecksum>(keysSql, parameters).SingleAsync(ct);
        var shape = await db.Database.SqlQueryRaw<JournalChecksum>(shapeSql, parameters).SingleAsync(ct);
        return $"{keys.Count}:{keys.Checksum:x8}{shape.Checksum:x8}";
    }

    private static async Task<string> JournalFingerprintStreamedAsync(TwinDbContext db, DateTimeOffset? before, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        await foreach (var e in db.ObservedEvents.AsNoTracking().OrderBy(e => e.IdempotencyKey).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (before is not null && e.OccurredAt >= before)
            {
                continue;
            }
            e.Id = 0;
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e, IntakeJson.Options)));
            count++;
        }
        return $"{count}:{Convert.ToHexStringLower(hash.GetHashAndReset())[..16]}";
    }

    private sealed record JournalChecksum(long Count, int Checksum);

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
