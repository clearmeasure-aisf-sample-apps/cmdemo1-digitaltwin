using DigitalTwin.Core.Observation;

namespace DigitalTwin.Core.ChangeChains;

/// <summary>
/// Attaches journal events to the chains they correlate with (GitHub <c>sha:</c>/<c>pr:</c> chain ids, Octopus release
/// ids): PR reviews, failed workflow jobs and checks, manual interventions, sign-offs, guided failures and failed deploys.
/// </summary>
public static class ChangeChainSignals
{
    public const int MaxPerChain = 12;

    public static readonly IReadOnlyList<string> EventTypes =
    [
        "dev.cdevents.change.reviewed",
        "dev.cdevents.taskRun.finished",
        "dev.cdevents.build.finished",
        "twin.octopus.interruption.manual",
        "twin.octopus.interruption.assigned",
        "twin.octopus.interruption.submitted",
        "twin.octopus.interruption.guidedFailure",
        "twin.octopus.deployment.failed"
    ];

    public static IReadOnlyList<ChangeChain> Attach(IReadOnlyList<ChangeChain> chains, IReadOnlyList<ObservedEvent> events)
    {
        var byChainId = events.Where(e => e.ChainId is not null && EventTypes.Contains(e.EventType))
            .GroupBy(e => e.ChainId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return chains.Select(chain =>
        {
            var signals = chain.CorrelationIds
                .SelectMany(id => byChainId.GetValueOrDefault(id) ?? [])
                .DistinctBy(e => e.IdempotencyKey)
                .Select(ToSignal)
                .Where(s => s is not null)
                .Select(s => s!)
                .OrderBy(s => s.At)
                .TakeLast(MaxPerChain)
                .ToList();
            return signals.Count == 0 ? chain : chain with { Signals = signals };
        }).ToList();
    }

    private static ChainSignal? ToSignal(ObservedEvent e)
    {
        ChainSignal Signal(ChainStageKind stage, string kind) => new(stage, kind, e.Summary, e.OccurredAt, e.Outcome.ToString(), e.Actor, e.SourceUrl);

        var deliveryStage = e.StageName?.ToLowerInvariant() switch
        {
            "tdd" => ChainStageKind.Tdd,
            "uat" => ChainStageKind.Uat,
            "prod" => ChainStageKind.Prod,
            _ => ChainStageKind.OctopusRelease
        };
        return e.EventType switch
        {
            "dev.cdevents.change.reviewed" => Signal(ChainStageKind.PullRequest, "review"),
            // Only failures: a green build already shows as a green stage.
            "dev.cdevents.taskRun.finished" when e.Outcome == EventOutcome.Failure => Signal(ChainStageKind.Build, "job-failed"),
            "dev.cdevents.build.finished" when e.Outcome == EventOutcome.Failure => Signal(ChainStageKind.Build, "check-failed"),
            "twin.octopus.interruption.manual" => Signal(deliveryStage, "manual-intervention"),
            "twin.octopus.interruption.assigned" => Signal(deliveryStage, "intervention-assigned"),
            "twin.octopus.interruption.submitted" => Signal(deliveryStage, "signed-off"),
            "twin.octopus.interruption.guidedFailure" => Signal(deliveryStage, "guided-failure"),
            "twin.octopus.deployment.failed" => Signal(deliveryStage, "deployment-failed"),
            _ => null
        };
    }
}
