using DigitalTwin.Application.Abstractions;
using DigitalTwin.Domain.BuildAndArtifact;
using DigitalTwin.Domain.Observation;
using DigitalTwin.Domain.ReleaseAndDelivery;
using DigitalTwin.Domain.ReliabilityAndDrift;
using DigitalTwin.Domain.RuntimeAndPlatform;
using DigitalTwin.Domain.SourceAndChange;
using DigitalTwin.Domain.Work;
using Microsoft.EntityFrameworkCore;

namespace DigitalTwin.Infrastructure.Persistence;

public sealed class EfTwinStore(IDbContextFactory<TwinDbContext> factory) : ITwinStore
{
    public async Task<bool> AppendEventIfNewAsync(ObservedEvent observedEvent, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.ObservedEvents.AnyAsync(e => e.IdempotencyKey == observedEvent.IdempotencyKey, ct);
        if (exists)
        {
            return false;
        }

        db.ObservedEvents.Add(observedEvent);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Concurrent webhook/poller race on unique IdempotencyKey
            return false;
        }
    }

    public async Task<int> AppendEventsIfNewAsync(IEnumerable<ObservedEvent> observedEvents, CancellationToken ct)
    {
        var count = 0;
        foreach (var ev in observedEvents)
        {
            if (await AppendEventIfNewAsync(ev, ct))
            {
                count++;
            }
        }
        return count;
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

        // Order by OccurredAt client-safe for SQLite DateTimeOffset support
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
        if (existing is null)
        {
            db.IngestionCursors.Add(cursor);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(cursor);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<IngestionCursor>> GetCursorsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.IngestionCursors.AsNoTracking().OrderBy(c => c.SourceKey).ToListAsync(ct);
    }

    public async Task ReplaceWebhookStatusesAsync(string source, IReadOnlyList<WebhookRegistrationStatus> statuses, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var old = await db.WebhookRegistrationStatuses.Where(w => w.Source == source).ToListAsync(ct);
        db.WebhookRegistrationStatuses.RemoveRange(old);
        db.WebhookRegistrationStatuses.AddRange(statuses);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookRegistrationStatus>> GetWebhookStatusesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WebhookRegistrationStatuses.AsNoTracking().OrderBy(w => w.Source).ThenBy(w => w.Target).ToListAsync(ct);
    }

    public async Task UpsertRepositoriesAsync(IReadOnlyList<RepositorySnapshot> repositories, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in repositories)
        {
            var existing = await db.Repositories.FindAsync([item.Name], ct);
            if (existing is null) db.Repositories.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RepositorySnapshot>> GetRepositoriesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Repositories.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
    }

    public async Task UpsertSourceChangesAsync(IReadOnlyList<SourceChangeSnapshot> changes, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in changes)
        {
            var existing = await db.SourceChanges.FindAsync([item.Id], ct);
            if (existing is null) db.SourceChanges.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SourceChangeSnapshot>> GetSourceChangesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.SourceChanges.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public async Task UpsertPipelineRunsAsync(IReadOnlyList<PipelineRunSnapshot> runs, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in runs)
        {
            var existing = await db.PipelineRuns.FindAsync([item.Id], ct);
            if (existing is null) db.PipelineRuns.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PipelineRunSnapshot>> GetPipelineRunsAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.PipelineRuns.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(r => r.UpdatedAt).Take(limit).ToList();
    }

    public async Task UpsertArtifactsAsync(IReadOnlyList<ArtifactPackageSnapshot> artifacts, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in artifacts)
        {
            var existing = await db.Artifacts.FindAsync([item.Id], ct);
            if (existing is null) db.Artifacts.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ArtifactPackageSnapshot>> GetArtifactsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Artifacts.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(a => a.PublishedAt).ToList();
    }

    public async Task UpsertWorkItemsAsync(IReadOnlyList<WorkItemSnapshot> items, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in items)
        {
            var existing = await db.WorkItems.FindAsync([item.Id], ct);
            if (existing is null) db.WorkItems.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkItemSnapshot>> GetWorkItemsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.WorkItems.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(w => w.UpdatedAt).ToList();
    }

    public async Task UpsertReleasesAsync(IReadOnlyList<ReleaseSnapshot> releases, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in releases)
        {
            var existing = await db.Releases.FindAsync([item.ReleaseId], ct);
            if (existing is null) db.Releases.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ReleaseSnapshot>> GetReleasesAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Releases.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    public async Task UpsertDeliveriesAsync(IReadOnlyList<DeliveryExecutionSnapshot> deliveries, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in deliveries)
        {
            var existing = await db.Deliveries.FindAsync([item.DeploymentId], ct);
            if (existing is null) db.Deliveries.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DeliveryExecutionSnapshot>> GetDeliveriesAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Deliveries.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(d => d.QueuedAt).Take(limit).ToList();
    }

    public async Task UpsertRunbooksAsync(IReadOnlyList<RunbookExecutionSnapshot> runs, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in runs)
        {
            var existing = await db.RunbookExecutions.FindAsync([item.TaskId], ct);
            if (existing is null) db.RunbookExecutions.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RunbookExecutionSnapshot>> GetRunbooksAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.RunbookExecutions.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(r => r.CreatedAt).Take(limit).ToList();
    }

    public async Task ReplaceFreezesAsync(IReadOnlyList<DeploymentFreezeSnapshot> freezes, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var all = await db.DeploymentFreezes.ToListAsync(ct);
        db.DeploymentFreezes.RemoveRange(all);
        db.DeploymentFreezes.AddRange(freezes);
        await db.SaveChangesAsync(ct);
    }

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
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in stages)
        {
            var existing = await db.EnvironmentInstances.FindAsync([item.StageName], ct);
            if (existing is null) db.EnvironmentInstances.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EnvironmentInstanceSnapshot>> GetEnvironmentInstancesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.EnvironmentInstances.AsNoTracking().ToListAsync(ct);
    }

    public async Task ReplaceWorkloadsAsync(IReadOnlyList<WorkloadRevisionSnapshot> workloads, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var old = await db.WorkloadRevisions.ToListAsync(ct);
        db.WorkloadRevisions.RemoveRange(old);
        db.WorkloadRevisions.AddRange(workloads);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkloadRevisionSnapshot>> GetWorkloadsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WorkloadRevisions.AsNoTracking().ToListAsync(ct);
    }

    public async Task ReplaceAzureResourcesAsync(IReadOnlyList<AzureResourceSnapshot> resources, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var old = await db.AzureResources.ToListAsync(ct);
        db.AzureResources.RemoveRange(old);
        db.AzureResources.AddRange(resources);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AzureResourceSnapshot>> GetAzureResourcesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AzureResources.AsNoTracking().OrderBy(r => r.ResourceGroup).ThenBy(r => r.Name).ToListAsync(ct);
    }

    public async Task UpsertHealthProbesAsync(IReadOnlyList<HealthProbeSnapshot> probes, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var item in probes)
        {
            var existing = await db.HealthProbes.FindAsync([item.TargetKey], ct);
            if (existing is null) db.HealthProbes.Add(item);
            else db.Entry(existing).CurrentValues.SetValues(item);
        }
        await db.SaveChangesAsync(ct);
    }

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
}
