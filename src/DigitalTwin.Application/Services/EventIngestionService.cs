using DigitalTwin.Application.Abstractions;
using DigitalTwin.Domain.Observation;
using DigitalTwin.Domain.ReliabilityAndDrift;

namespace DigitalTwin.Application.Services;

public sealed class EventIngestionService(
    ITwinStore store,
    IRealtimeEventPublisher realtime,
    TimeProvider timeProvider)
{
    public async Task<bool> IngestAsync(ObservedEvent observedEvent, CancellationToken ct)
    {
        var added = await store.AppendEventIfNewAsync(observedEvent, ct);
        if (!added)
        {
            return false;
        }

        await realtime.PublishEventAsync(observedEvent, ct);
        return true;
    }

    public async Task<int> IngestBatchAsync(IEnumerable<ObservedEvent> events, CancellationToken ct)
    {
        var list = events.OrderBy(e => e.OccurredAt).ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        var newlyAdded = await store.AppendEventsReturnNewAsync(list, ct);
        foreach (var ev in newlyAdded)
        {
            await realtime.PublishEventAsync(ev, ct);
        }

        return newlyAdded.Count;
    }

    public async Task<IReadOnlyList<DriftFinding>> EvaluateAndRecordDriftAsync(CancellationToken ct)
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

        foreach (var finding in evaluated)
        {
            if (!previousActive.Contains(finding.DriftKey))
            {
                await IngestAsync(new ObservedEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
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
                }, ct);
            }
        }

        await realtime.PublishStateRefreshedAsync("drift-evaluated", now, ct);
        return evaluated;
    }
}
