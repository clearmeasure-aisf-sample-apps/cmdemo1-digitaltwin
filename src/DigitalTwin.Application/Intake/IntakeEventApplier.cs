using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Application.Intake;

/// <summary>Extension point invoked before a raw event is applied (fault injection, auditing).</summary>
public interface IIntakeApplyInterceptor
{
    ValueTask BeforeApplyAsync(RawIntakeEvent rawEvent, CancellationToken ct);
}

/// <summary>
/// Applies captured raw events to the twin model. Idempotent: events are deduplicated on their
/// idempotency key, so redeliveries, replays and webhook/poll overlap never double-count.
/// </summary>
public sealed class IntakeEventApplier(
    IRawIntakeStore rawStore,
    ITwinStore store,
    IRealtimeEventPublisher realtime,
    FollowUpRefreshScheduler followUps,
    IEnumerable<IIntakeApplyInterceptor> interceptors,
    TimeProvider timeProvider) : IIntakeEventApplier
{
    private static readonly HashSet<string> GitHubRefreshEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "push", "pull_request", "workflow_run", "issues", "release", "deployment", "deployment_status"
    };

    public async Task<IntakeApplyResult> ApplyAsync(Guid rawEventId, CancellationToken ct)
    {
        var raw = await rawStore.GetAsync(rawEventId, ct)
            ?? throw new InvalidOperationException($"Raw intake event {rawEventId} was not found.");

        if (raw.ProcessedAt is not null)
        {
            return new IntakeApplyResult(raw.Id, 0, 0, AlreadyProcessed: true);
        }

        var (normalized, applied) = await ApplyRawAsync(raw, live: true, ct);
        return new IntakeApplyResult(raw.Id, normalized.Count, applied.Count, AlreadyProcessed: false);
    }

    internal async Task<(IReadOnlyList<ObservedEvent> Normalized, IReadOnlyList<ObservedEvent> Applied)> ApplyRawAsync(
        RawIntakeEvent raw,
        bool live,
        CancellationToken ct)
    {
        foreach (var interceptor in interceptors)
        {
            await interceptor.BeforeApplyAsync(raw, ct);
        }

        var normalized = IntakeNormalizer.Normalize(raw);
        var applied = await store.AppendEventsReturnNewAsync(normalized.OrderBy(e => e.OccurredAt), ct);
        await rawStore.MarkProcessedAsync(raw.Id, applied.Count, timeProvider.GetUtcNow(), ct);

        if (live)
        {
            foreach (var ev in applied)
            {
                await realtime.PublishEventAsync(ev, ct);
            }
            RequestFollowUps(raw, normalized);
        }

        return (normalized, applied);
    }

    private void RequestFollowUps(RawIntakeEvent raw, IReadOnlyList<ObservedEvent> normalized)
    {
        switch (raw.Kind)
        {
            case IntakeKind.GitHubWebhook:
                if (GitHubRefreshEvents.Contains(raw.Header("X-GitHub-Event") ?? string.Empty))
                {
                    var repo = (normalized.Count > 0 ? normalized[0] : null)?.SubjectId.Split(':', '#')[0].Split('/').LastOrDefault();
                    if (!string.IsNullOrWhiteSpace(repo))
                    {
                        followUps.Request(FollowUpRefresh.GitHubRepository(repo));
                    }
                }
                break;
            case IntakeKind.OctopusWebhook:
                if (normalized.Count > 0)
                {
                    followUps.Request(FollowUpRefresh.Octopus);
                }
                break;
            case IntakeKind.AzureWebhook:
                if (normalized.Count > 0)
                {
                    followUps.Request(FollowUpRefresh.Azure);
                }
                break;
            case IntakeKind.ObservedBatch:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(raw), raw.Kind, "Unknown intake kind");
        }
    }
}
