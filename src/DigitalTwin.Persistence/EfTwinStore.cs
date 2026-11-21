using DigitalTwin.Core.Ports;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;
using Microsoft.EntityFrameworkCore;

namespace DigitalTwin.Persistence;

public sealed class EfTwinStore(IDbContextFactory<TwinDbContext> factory) : ITwinStore
{
    public async Task<bool> AppendEventIfNewAsync(ObservedEvent observedEvent, CancellationToken ct)
    {
        var added = await AppendEventsReturnNewAsync([observedEvent], ct);
        return added.Count > 0;
    }

    public async Task<int> AppendEventsIfNewAsync(IEnumerable<ObservedEvent> observedEvents, CancellationToken ct)
    {
        var added = await AppendEventsReturnNewAsync(observedEvents, ct);
        return added.Count;
    }

    /// <summary>
    /// Appends events whose idempotency key is new and returns them. For keys already present the earliest
    /// observation is kept (<see cref="ObservedEventPrecedence"/>), so the result does not depend on processing order.
    /// </summary>
    public async Task<IReadOnlyList<ObservedEvent>> AppendEventsReturnNewAsync(IEnumerable<ObservedEvent> observedEvents, CancellationToken ct)
    {
        var candidates = observedEvents
            .GroupBy(e => e.IdempotencyKey, StringComparer.Ordinal)
            .Select(ObservedEventPrecedence.Earliest)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        try
        {
            return await MergeAsync(candidates, ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent handler inserted one of the keys; merge one by one against the committed rows.
            var inserted = new List<ObservedEvent>();
            foreach (var candidate in candidates)
            {
                try
                {
                    inserted.AddRange(await MergeAsync([candidate], ct));
                }
                catch (DbUpdateException)
                {
                    inserted.AddRange(await MergeAsync([candidate], ct));
                }
            }
            return inserted;
        }
    }

    private async Task<IReadOnlyList<ObservedEvent>> MergeAsync(IReadOnlyList<ObservedEvent> candidates, CancellationToken ct)
    {
        var keys = candidates.Select(e => e.IdempotencyKey).ToList();
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.ObservedEvents
            .Where(e => keys.Contains(e.IdempotencyKey))
            .ToDictionaryAsync(e => e.IdempotencyKey, StringComparer.Ordinal, ct);

        var inserted = new List<ObservedEvent>();
        foreach (var candidate in candidates)
        {
            if (existing.TryGetValue(candidate.IdempotencyKey, out var current))
            {
                if (ObservedEventPrecedence.Precedes(candidate, current))
                {
                    var replacement = Clone(candidate);
                    replacement.Id = current.Id;
                    db.Entry(current).CurrentValues.SetValues(replacement);
                }
                continue;
            }

            var row = Clone(candidate);
            db.ObservedEvents.Add(row);
            inserted.Add(row);
        }

        await db.SaveChangesAsync(ct);
        return inserted;
    }

    private static ObservedEvent Clone(ObservedEvent e) => new()
    {
        EventId = e.EventId,
        IdempotencyKey = e.IdempotencyKey,
        Source = e.Source,
        SourceEventId = e.SourceEventId,
        SpecVersion = e.SpecVersion,
        EventType = e.EventType,
        SubjectType = e.SubjectType,
        SubjectId = e.SubjectId,
        Predicate = e.Predicate,
        OccurredAt = e.OccurredAt,
        IngestedAt = e.IngestedAt,
        Channel = e.Channel,
        CorrelationId = e.CorrelationId,
        ChainId = e.ChainId,
        PipelineId = e.PipelineId,
        StageName = e.StageName,
        DeployableName = e.DeployableName,
        Actor = e.Actor,
        Outcome = e.Outcome,
        Summary = e.Summary,
        SourceUrl = e.SourceUrl,
        PayloadJson = e.PayloadJson
    };

    public async Task<IReadOnlySet<string>> FindExistingIdempotencyKeysAsync(IReadOnlyCollection<string> idempotencyKeys, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (idempotencyKeys.Count == 0)
        {
            return result;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var chunk in idempotencyKeys.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var found = await db.ObservedEvents
                .AsNoTracking()
                .Where(e => chunk.Contains(e.IdempotencyKey))
                .Select(e => e.IdempotencyKey)
                .ToListAsync(ct);
            result.UnionWith(found);
        }
        return result;
    }

    public async Task<int> ClearEventJournalAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ObservedEvents.ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<ObservedEvent>> QueryEventsAsync(
        DateTimeOffset? since,
        EventSourceKind? source,
        string? pipelineId,
        string? stageName,
        string? eventType,
        int limit,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.ObservedEvents.AsNoTracking().AsQueryable();

        if (since is not null)
        {
            query = query.Where(e => e.OccurredAt >= since.Value);
        }
        if (source is not null)
        {
            query = query.Where(e => e.Source == source.Value);
        }
        if (!string.IsNullOrWhiteSpace(pipelineId))
        {
            query = query.Where(e => e.PipelineId == pipelineId);
        }
        if (!string.IsNullOrWhiteSpace(stageName))
        {
            query = query.Where(e => e.StageName == stageName);
        }
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(e => e.EventType == eventType);
        }

        var rows = await query.ToListAsync(ct);
        return rows
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToList();
    }

    public async Task UpsertCursorAsync(IngestionCursor cursor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.IngestionCursors.FindAsync([cursor.SourceKey], ct);
        if (existing is null) db.IngestionCursors.Add(cursor);
        else db.Entry(existing).CurrentValues.SetValues(cursor);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<IngestionCursor>> GetCursorsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.IngestionCursors.AsNoTracking().OrderBy(c => c.SourceKey).ToListAsync(ct);
    }

    public async Task ReplaceWebhookStatusesAsync(string source, IReadOnlyList<WebhookRegistrationStatus> statuses, CancellationToken ct)
    {
        var deduped = statuses.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        var keepIds = deduped.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var existing = await db.WebhookRegistrationStatuses
                .Where(w => w.Source == source)
                .ToDictionaryAsync(w => w.Id, StringComparer.OrdinalIgnoreCase, ct);

            foreach (var stale in existing.Values.Where(w => !keepIds.Contains(w.Id)))
            {
                db.WebhookRegistrationStatuses.Remove(stale);
            }

            foreach (var item in deduped)
            {
                if (existing.TryGetValue(item.Id, out var found))
                {
                    db.Entry(found).CurrentValues.SetValues(item);
                }
                else
                {
                    db.WebhookRegistrationStatuses.Add(item);
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                // Retry once if an overlapping revision deleted/updated rows concurrently
            }
        }
    }

    public async Task<IReadOnlyList<WebhookRegistrationStatus>> GetWebhookStatusesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WebhookRegistrationStatuses.AsNoTracking().OrderBy(w => w.Source).ThenBy(w => w.Target).ToListAsync(ct);
    }

    public async Task UpsertRepositoriesAsync(IReadOnlyList<RepositorySnapshot> repositories, CancellationToken ct)
        => await BatchUpsertAsync(
            repositories,
            r => r.Name,
            (db, ids, token) => db.Repositories.Where(x => ids.Contains(x.Name)).ToDictionaryAsync(x => x.Name, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.Repositories.Add(item),
            ct);

    public async Task<IReadOnlyList<RepositorySnapshot>> GetRepositoriesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Repositories.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
    }

    public async Task UpsertSourceChangesAsync(IReadOnlyList<SourceChangeSnapshot> changes, CancellationToken ct)
        => await BatchUpsertAsync(
            changes,
            c => c.Id,
            (db, ids, token) => db.SourceChanges.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.SourceChanges.Add(item),
            ct);

    public async Task<IReadOnlyList<SourceChangeSnapshot>> GetSourceChangesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.SourceChanges.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public async Task UpsertPipelineRunsAsync(IReadOnlyList<PipelineRunSnapshot> runs, CancellationToken ct)
        => await BatchUpsertAsync(
            runs,
            r => r.Id,
            (db, ids, token) => db.PipelineRuns.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.PipelineRuns.Add(item),
            ct);

    public async Task<IReadOnlyList<PipelineRunSnapshot>> GetPipelineRunsAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.PipelineRuns.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(r => r.UpdatedAt).Take(limit).ToList();
    }

    public async Task UpsertArtifactsAsync(IReadOnlyList<ArtifactPackageSnapshot> artifacts, CancellationToken ct)
        => await BatchUpsertAsync(
            artifacts,
            a => a.Id,
            (db, ids, token) => db.Artifacts.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.Artifacts.Add(item),
            ct);

    public async Task<IReadOnlyList<ArtifactPackageSnapshot>> GetArtifactsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Artifacts.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(a => a.PublishedAt).ToList();
    }

    public async Task UpsertWorkItemsAsync(IReadOnlyList<WorkItemSnapshot> items, CancellationToken ct)
        => await BatchUpsertAsync(
            items,
            w => w.Id,
            (db, ids, token) => db.WorkItems.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.WorkItems.Add(item),
            ct);

    public async Task<IReadOnlyList<WorkItemSnapshot>> GetWorkItemsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.WorkItems.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(w => w.UpdatedAt).ToList();
    }

    public async Task UpsertReleasesAsync(IReadOnlyList<ReleaseSnapshot> releases, CancellationToken ct)
        => await BatchUpsertAsync(
            releases,
            r => r.ReleaseId,
            (db, ids, token) => db.Releases.Where(x => ids.Contains(x.ReleaseId)).ToDictionaryAsync(x => x.ReleaseId, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.Releases.Add(item),
            ct);

    public async Task<IReadOnlyList<ReleaseSnapshot>> GetReleasesAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Releases.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    public async Task UpsertDeliveriesAsync(IReadOnlyList<DeliveryExecutionSnapshot> deliveries, CancellationToken ct)
        => await BatchUpsertAsync(
            deliveries,
            d => d.DeploymentId,
            (db, ids, token) => db.Deliveries.Where(x => ids.Contains(x.DeploymentId)).ToDictionaryAsync(x => x.DeploymentId, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.Deliveries.Add(item),
            ct);

    public async Task<IReadOnlyList<DeliveryExecutionSnapshot>> GetDeliveriesAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Deliveries.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(d => d.QueuedAt).Take(limit).ToList();
    }

    public async Task UpsertRunbooksAsync(IReadOnlyList<RunbookExecutionSnapshot> runs, CancellationToken ct)
        => await BatchUpsertAsync(
            runs,
            r => r.TaskId,
            (db, ids, token) => db.RunbookExecutions.Where(x => ids.Contains(x.TaskId)).ToDictionaryAsync(x => x.TaskId, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.RunbookExecutions.Add(item),
            ct);

    public async Task<IReadOnlyList<RunbookExecutionSnapshot>> GetRunbooksAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.RunbookExecutions.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    public async Task ReplaceFreezesAsync(IReadOnlyList<DeploymentFreezeSnapshot> freezes, CancellationToken ct)
        => await ReplaceSetAsync(
            freezes,
            f => f.FreezeId,
            (db, token) => db.DeploymentFreezes.ToListAsync(token),
            (db, item) => db.DeploymentFreezes.Add(item),
            (db, item) => db.DeploymentFreezes.Remove(item),
            ct);

    public async Task<IReadOnlyList<DeploymentFreezeSnapshot>> GetFreezesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentFreezes.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertDesiredConfigAsync(DesiredSystemConfigSnapshot config, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.DesiredSystemConfigs.FindAsync([config.Id], ct);
        if (existing is null) db.DesiredSystemConfigs.Add(config);
        else db.Entry(existing).CurrentValues.SetValues(config);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DesiredSystemConfigSnapshot?> GetDesiredConfigAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DesiredSystemConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == "cmdemo1", ct);
    }

    public async Task UpsertEnvironmentInstancesAsync(IReadOnlyList<EnvironmentInstanceSnapshot> stages, CancellationToken ct)
        => await BatchUpsertAsync(
            stages,
            s => s.StageName,
            (db, ids, token) => db.EnvironmentInstances.Where(x => ids.Contains(x.StageName)).ToDictionaryAsync(x => x.StageName, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.EnvironmentInstances.Add(item),
            ct);

    public async Task<IReadOnlyList<EnvironmentInstanceSnapshot>> GetEnvironmentInstancesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.EnvironmentInstances.AsNoTracking().ToListAsync(ct);
    }

    public async Task ReplaceWorkloadsAsync(IReadOnlyList<WorkloadRevisionSnapshot> workloads, CancellationToken ct)
        => await ReplaceSetAsync(
            workloads,
            w => w.Id,
            (db, token) => db.WorkloadRevisions.ToListAsync(token),
            (db, item) => db.WorkloadRevisions.Add(item),
            (db, item) => db.WorkloadRevisions.Remove(item),
            ct);

    public async Task<IReadOnlyList<WorkloadRevisionSnapshot>> GetWorkloadsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WorkloadRevisions.AsNoTracking().ToListAsync(ct);
    }

    public async Task ReplaceAzureResourcesAsync(IReadOnlyList<AzureResourceSnapshot> resources, CancellationToken ct)
        => await ReplaceSetAsync(
            resources,
            r => r.ResourceId,
            (db, token) => db.AzureResources.ToListAsync(token),
            (db, item) => db.AzureResources.Add(item),
            (db, item) => db.AzureResources.Remove(item),
            ct);

    public async Task<IReadOnlyList<AzureResourceSnapshot>> GetAzureResourcesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AzureResources.AsNoTracking().OrderBy(r => r.ResourceGroup).ThenBy(r => r.Name).ToListAsync(ct);
    }

    public async Task UpsertHealthProbesAsync(IReadOnlyList<HealthProbeSnapshot> probes, CancellationToken ct)
        => await BatchUpsertAsync(
            probes,
            p => p.TargetKey,
            (db, ids, token) => db.HealthProbes.Where(x => ids.Contains(x.TargetKey)).ToDictionaryAsync(x => x.TargetKey, StringComparer.OrdinalIgnoreCase, token),
            (db, item) => db.HealthProbes.Add(item),
            ct);

    public async Task<IReadOnlyList<HealthProbeSnapshot>> GetHealthProbesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.HealthProbes.AsNoTracking().ToListAsync(ct);
    }

    public async Task ReconcileDriftFindingsAsync(IReadOnlyList<DriftFinding> evaluated, DateTimeOffset evaluatedAt, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.DriftFindings.ToDictionaryAsync(d => d.DriftKey, StringComparer.OrdinalIgnoreCase, ct);
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in evaluated)
        {
            activeKeys.Add(item.DriftKey);
            if (existing.TryGetValue(item.DriftKey, out var current))
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
                    current.DetectedAt = evaluatedAt;
                }
                current.IsActive = true;
                current.ResolvedAt = null;
            }
            else
            {
                db.DriftFindings.Add(item);
            }
        }

        foreach (var current in existing.Values.Where(d => d.IsActive && !activeKeys.Contains(d.DriftKey)))
        {
            current.IsActive = false;
            current.ResolvedAt = evaluatedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DriftFinding>> GetDriftFindingsAsync(bool includeResolved, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.DriftFindings.AsNoTracking();
        if (!includeResolved)
        {
            query = query.Where(d => d.IsActive);
        }
        var list = await query.ToListAsync(ct);
        return list.OrderByDescending(d => d.Severity).ThenByDescending(d => d.DetectedAt).ToList();
    }

    private async Task BatchUpsertAsync<TEntity>(
        IReadOnlyList<TEntity> items,
        Func<TEntity, string> keySelector,
        Func<TwinDbContext, List<string>, CancellationToken, Task<Dictionary<string, TEntity>>> loadExisting,
        Action<TwinDbContext, TEntity> addEntity,
        CancellationToken ct) where TEntity : class
    {
        var deduped = items
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        if (deduped.Count == 0)
        {
            return;
        }

        var ids = deduped.Select(keySelector).ToList();
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await loadExisting(db, ids, ct);

        foreach (var item in deduped)
        {
            var id = keySelector(item);
            if (existing.TryGetValue(id, out var found))
            {
                db.Entry(found).CurrentValues.SetValues(item);
            }
            else
            {
                addEntity(db, item);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ReplaceSetAsync<TEntity>(
        IReadOnlyList<TEntity> items,
        Func<TEntity, string> keySelector,
        Func<TwinDbContext, CancellationToken, Task<List<TEntity>>> loadAll,
        Action<TwinDbContext, TEntity> addEntity,
        Action<TwinDbContext, TEntity> removeEntity,
        CancellationToken ct) where TEntity : class
    {
        var deduped = items.GroupBy(keySelector, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        var keepIds = deduped.Select(keySelector).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var existingList = await loadAll(db, ct);
            var existing = existingList.ToDictionary(keySelector, StringComparer.OrdinalIgnoreCase);

            foreach (var stale in existing.Values.Where(x => !keepIds.Contains(keySelector(x))))
            {
                removeEntity(db, stale);
            }

            foreach (var item in deduped)
            {
                var id = keySelector(item);
                if (existing.TryGetValue(id, out var found))
                {
                    db.Entry(found).CurrentValues.SetValues(item);
                }
                else
                {
                    addEntity(db, item);
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                // Retry once if overlapping container revisions updated rows concurrently
            }
        }
    }
}
