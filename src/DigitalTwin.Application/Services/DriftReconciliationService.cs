using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.ReliabilityAndDrift;

namespace DigitalTwin.Application.Services;

/// <summary>Evaluates desired-vs-observed drift and publishes newly opened findings through the intake.</summary>
public sealed class DriftReconciliationService(
    ITwinStore store,
    ObservedEventIntake intake,
    IRealtimeEventPublisher realtime,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<DriftFinding>> EvaluateAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var desired = await store.GetDesiredConfigAsync(ct);
        var workloads = await store.GetWorkloadsAsync(ct);
        var probes = await store.GetHealthProbesAsync(ct);
        var resources = await store.GetAzureResourcesAsync(ct);
        var repos = await store.GetRepositoriesAsync(ct);

        var previousActive = (await store.GetDriftFindingsAsync(includeResolved: false, ct))
            .Select(d => d.DriftKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var evaluated = DriftEvaluator.Evaluate(desired, workloads, probes, resources, repos, now);
        await store.ReconcileDriftFindingsAsync(evaluated, now, ct);

        var opened = evaluated
            .Where(f => !previousActive.Contains(f.DriftKey))
            .Select(finding => new ObservedEvent
            {
                EventId = $"drift-{finding.DriftKey}-{now:yyyyMMddHHmm}",
                IdempotencyKey = $"drift-open:{finding.DriftKey}:{now:yyyyMMddHHmm}",
                Source = EventSourceKind.Twin,
                SourceEventId = $"{finding.DriftKey}:{now:O}",
                EventType = "twin.drift.detected",
                SubjectType = "drift",
                SubjectId = finding.DriftKey,
                Predicate = "detected",
                OccurredAt = now,
                IngestedAt = now,
                Channel = IngestionChannel.Probe,
                PipelineId = finding.DeployableName is null ? "system-gitops" : $"app-{finding.DeployableName}",
                StageName = finding.StageName,
                DeployableName = finding.DeployableName,
                Actor = "twin-reconciler",
                Outcome = finding.Severity == DriftSeverity.Critical ? EventOutcome.Failure : EventOutcome.Waiting,
                Summary = finding.Summary
            })
            .ToList();

        await intake.PublishAsync(opened, EventSourceKind.Twin, IngestionChannel.Probe, ct);
        await realtime.PublishStateRefreshedAsync("drift-evaluated", now, ct);
        return evaluated;
    }
}
