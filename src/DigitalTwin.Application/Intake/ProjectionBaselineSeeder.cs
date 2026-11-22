using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Application.Intake;

/// <summary>
/// One-time migration to event-sourced read models: rows written before projections existed are captured as
/// state observations, so the raw store holds everything a rebuild needs (e.g. deployments older than the
/// Octopus poll window).
/// </summary>
public sealed class ProjectionBaselineSeeder(ITwinStore store, StateObservationPublisher state)
{
    public async Task<bool> SeedIfNeededAsync(CancellationToken ct)
    {
        if (await store.CountProjectionVersionsAsync(ct) > 0)
        {
            return false;
        }

        const EventSourceKind source = EventSourceKind.Twin;
        await state.PublishAsync(StateKinds.Repository, await store.GetRepositoriesAsync(ct), source, ct);
        await state.PublishAsync(StateKinds.SourceChange, await store.GetSourceChangesAsync(ct), source, ct);
        await state.PublishAsync(StateKinds.PipelineRun, await store.GetPipelineRunsAsync(int.MaxValue, ct), source, ct);
        await state.PublishAsync(StateKinds.Artifact, await store.GetArtifactsAsync(ct), source, ct);
        await state.PublishAsync(StateKinds.WorkItem, await store.GetWorkItemsAsync(ct), source, ct);
        await state.PublishAsync(StateKinds.Release, await store.GetReleasesAsync(int.MaxValue, ct), source, ct);
        await state.PublishAsync(StateKinds.Delivery, await store.GetDeliveriesAsync(int.MaxValue, ct), source, ct);
        await state.PublishAsync(StateKinds.Runbook, await store.GetRunbooksAsync(int.MaxValue, ct), source, ct);
        await state.PublishAsync(StateKinds.Environment, await store.GetEnvironmentInstancesAsync(ct), source, ct);
        await state.PublishAsync(StateKinds.HealthProbe, await store.GetHealthProbesAsync(ct), source, ct);
        if (await store.GetDesiredConfigAsync(ct) is { } desired)
        {
            await state.PublishAsync(StateKinds.DesiredConfig, [desired], source, ct);
        }

        await PublishSetIfAnyAsync(StateKinds.Freeze, "octopus", await store.GetFreezesAsync(ct), ct);
        await PublishSetIfAnyAsync(StateKinds.Workload, "azure", await store.GetWorkloadsAsync(ct), ct);
        await PublishSetIfAnyAsync(StateKinds.AzureResource, "azure", await store.GetAzureResourcesAsync(ct), ct);
        await PublishSetIfAnyAsync(StateKinds.Drift, Cmdemo1PlatformCatalog.SystemSlug, await store.GetDriftFindingsAsync(includeResolved: false, ct), ct);
        foreach (var group in (await store.GetWebhookStatusesAsync(ct)).GroupBy(w => w.Source))
        {
            await PublishSetIfAnyAsync(StateKinds.WebhookStatus, group.Key, group.ToList(), ct);
        }
        return true;
    }

    private async Task PublishSetIfAnyAsync<T>(StateKind kind, string scope, IReadOnlyList<T> items, CancellationToken ct) where T : class
    {
        if (items.Count > 0)
        {
            await state.PublishSetAsync(kind, scope, items, EventSourceKind.Twin, ct);
        }
    }
}
