using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Application.Intake;

/// <summary>Extension point invoked before a raw event is applied (fault injection, auditing).</summary>
public interface IIntakeApplyInterceptor
{
    ValueTask BeforeApplyAsync(RawIntakeEvent rawEvent, CancellationToken ct);
}

internal sealed record IntakeReplayResult(int Replayed, int Appended, IReadOnlyList<string> Failures);

/// <summary>
/// Applies captured raw events to the twin model. Idempotent: events are deduplicated on their
/// idempotency key, so redeliveries, replays and webhook/poll overlap never double-count.
/// </summary>
public sealed class IntakeEventApplier(
    IRawIntakeStore rawStore,
    ITwinStore store,
    IRealtimeEventPublisher realtime,
    FollowUpRefreshScheduler followUps,
    ProjectionGate gate,
    IEnumerable<IIntakeApplyInterceptor> interceptors,
    TimeProvider timeProvider) : IIntakeEventApplier
{
    private const int ReplayAppendChunk = 1000;

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

        using var entered = await gate.TryEnterLiveAsync(ct)
            ?? throw new IntakeApplyDeferredException($"Raw intake event {raw.Id} deferred: a projection rebuild is running.");
        var (normalized, applied) = await ApplyRawAsync(raw, ct);
        return new IntakeApplyResult(raw.Id, normalized.Count, applied.Count, AlreadyProcessed: false);
    }

    /// <summary>
    /// Rebuild path: replays already-processed raw events into <paramref name="target"/> (the shadow store) without side effects. Appending (earliest observation wins)
    /// and projecting (latest wins) do not depend on order, so a whole page is written at once.
    /// </summary>
    internal async Task<IntakeReplayResult> ReplayAsync(IReadOnlyList<RawIntakeEvent> rawEvents, ITwinStore target, CancellationToken ct)
    {
        var normalized = new List<ObservedEvent>();
        var failures = new List<string>();
        var replayed = 0;
        foreach (var raw in rawEvents)
        {
            try
            {
                foreach (var interceptor in interceptors)
                {
                    await interceptor.BeforeApplyAsync(raw, ct);
                }
                normalized.AddRange(IntakeNormalizer.Normalize(raw));
                replayed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures.Add($"{raw.Id}: {ex.GetType().Name}: {ex.GetBaseException().Message}");
            }
        }

        var appended = 0;
        foreach (var chunk in normalized.OrderBy(e => e.OccurredAt).Chunk(ReplayAppendChunk))
        {
            appended += (await target.AppendEventsReturnNewAsync(chunk, ct)).Count;
        }
        var stateEvents = normalized.Where(StateObservations.IsStateEvent).ToList();
        if (stateEvents.Count > 0)
        {
            await target.ApplyStateObservationsAsync(stateEvents, ct);
        }
        return new IntakeReplayResult(replayed, appended, failures);
    }

    private async Task<(IReadOnlyList<ObservedEvent> Normalized, IReadOnlyList<ObservedEvent> Applied)> ApplyRawAsync(
        RawIntakeEvent raw,
        CancellationToken ct)
    {
        foreach (var interceptor in interceptors)
        {
            await interceptor.BeforeApplyAsync(raw, ct);
        }

        var normalized = IntakeNormalizer.Normalize(raw);
        var applied = await store.AppendEventsReturnNewAsync(normalized.OrderBy(e => e.OccurredAt), ct);

        // Idempotent (latest-wins per entity), so re-applying after a crash or redelivery is harmless.
        var stateEvents = normalized.Where(StateObservations.IsStateEvent).ToList();
        var projected = stateEvents.Count > 0 ? await store.ApplyStateObservationsAsync(stateEvents, ct) : 0;
        await rawStore.MarkProcessedAsync(raw.Id, applied.Count, timeProvider.GetUtcNow(), ct);

        foreach (var ev in applied.Where(e => !StateObservations.IsStateEvent(e)))
        {
            await realtime.PublishEventAsync(ev, ct);
        }
        if (projected > 0)
        {
            await BroadcastStateChangedAsync(ct);
        }
        RequestFollowUps(raw, normalized);

        return (normalized, applied);
    }

    private long _lastStateBroadcastTicks;

    private async Task BroadcastStateChangedAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var last = Interlocked.Read(ref _lastStateBroadcastTicks);
        if (now.UtcTicks - last < TimeSpan.FromSeconds(3).Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref _lastStateBroadcastTicks, now.UtcTicks);
        await realtime.PublishStateRefreshedAsync("state-observed", now, ct);
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
