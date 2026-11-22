namespace DigitalTwin.Core.ChangeChains;

public sealed record DoraMetric(double? Value, string Unit, string Rating);

public sealed record DeployableDora(string Deployable, int Deployments, int FailedDeployments, double? MedianLeadTimeHours);

/// <summary>The four DORA keys computed from change chains (one chain = one change), with DORA performance bands.</summary>
public sealed record ChainDoraMetrics(
    DateTimeOffset AsOf,
    int WindowDays,
    string? Deployable,
    int Deployments,
    int FailedDeployments,
    DoraMetric DeploymentFrequency,
    DoraMetric LeadTimeForChanges,
    DoraMetric LeadTimeP85,
    DoraMetric ChangeFailureRate,
    DoraMetric TimeToRestore,
    IReadOnlyList<DeployableDora> ByDeployable,
    IReadOnlyList<string> FailedChains);

public static class ChainDora
{
    private static readonly string[] FailureSignals = ["guided-failure", "deployment-failed"];

    /// <summary>
    /// A deployment is a chain whose prod stage started in the window. It failed when prod's latest attempt failed, any
    /// prod attempt failed, or a guided-failure or failed-deploy signal hit prod. Time to restore runs from that failure
    /// to the next successful prod delivery of the same deployable.
    /// </summary>
    public static ChainDoraMetrics Calculate(IReadOnlyList<ChangeChain> chains, DateTimeOffset asOf, int windowDays, string? deployable = null)
    {
        var from = asOf.AddDays(-windowDays);
        var prodChains = chains
            .Where(c => deployable is null || string.Equals(c.Deployable, deployable, StringComparison.OrdinalIgnoreCase))
            .Select(c => (Chain: c, Prod: c.Stages.FirstOrDefault(s => s.Kind == ChainStageKind.Prod)))
            .Where(x => x.Prod is { StartedAt: not null } && x.Prod.Status != ChainStageStatus.Pending && x.Prod.StartedAt <= asOf)
            .ToList();
        var inWindow = prodChains.Where(x => x.Prod!.StartedAt >= from).ToList();

        bool Failed((ChangeChain Chain, ChainStage? Prod) x)
            => x.Prod!.Status == ChainStageStatus.Failed || x.Prod.FailedAttempts > 0 ||
               (x.Chain.Signals ?? []).Any(s => s.Stage == ChainStageKind.Prod && FailureSignals.Contains(s.Kind));

        var delivered = inWindow.Where(x => x.Prod!.Status == ChainStageStatus.Succeeded).ToList();
        var failed = inWindow.Where(Failed).ToList();
        var leadTimes = delivered.Select(x => x.Chain.LeadTimeHours).Where(h => h is not null).Select(h => h!.Value).Order().ToList();

        // Restore: failure time -> next successful prod delivery of that deployable (any chain, possibly the same one retried).
        var successesByDeployable = prodChains.Where(x => x.Prod!.Status == ChainStageStatus.Succeeded && x.Prod.FinishedAt is not null)
            .GroupBy(x => x.Chain.Deployable ?? x.Chain.PipelineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Prod!.FinishedAt!.Value).Order().ToList(), StringComparer.OrdinalIgnoreCase);
        var restores = failed.Select(x =>
            {
                var failedAt = x.Prod!.FinishedAt ?? x.Prod.StartedAt!.Value;
                var next = successesByDeployable.GetValueOrDefault(x.Chain.Deployable ?? x.Chain.PipelineId)?.FirstOrDefault(t => t >= failedAt);
                return next is { } n && n != default ? (double?)(n - failedAt).TotalHours : null;
            })
            .Where(h => h is not null).Select(h => h!.Value).Order().ToList();

        var perDay = Math.Round(delivered.Count / (double)windowDays, 2);
        double? lead = Percentile(leadTimes, 0.5);
        double? leadP85 = Percentile(leadTimes, 0.85);
        double? cfr = inWindow.Count == 0 ? null : Math.Round(100.0 * failed.Count / inWindow.Count, 1);
        double? restore = Percentile(restores, 0.5);

        var byDeployable = inWindow.GroupBy(x => x.Chain.Deployable ?? x.Chain.PipelineId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DeployableDora(g.Key, g.Count(), g.Count(Failed),
                Percentile(g.Where(x => x.Prod!.Status == ChainStageStatus.Succeeded).Select(x => x.Chain.LeadTimeHours).Where(h => h is not null).Select(h => h!.Value).Order().ToList(), 0.5)))
            .OrderByDescending(d => d.Deployments)
            .ToList();

        return new ChainDoraMetrics(asOf, windowDays, deployable, inWindow.Count, failed.Count,
            new DoraMetric(perDay, "per day", FrequencyRating(perDay)),
            new DoraMetric(lead, "hours", LeadRating(lead)),
            new DoraMetric(leadP85, "hours", LeadRating(leadP85)),
            new DoraMetric(cfr, "percent", FailureRating(cfr)),
            new DoraMetric(restore is null ? null : Math.Round(restore.Value, 2), "hours", RestoreRating(restore)),
            byDeployable,
            failed.Select(x => x.Chain.ChainId).ToList());
    }

    private static double? Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return null;
        }
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return Math.Round(sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo)), 2);
    }

    // Bands from the DORA State of DevOps research (elite / high / medium / low).
    private static string FrequencyRating(double perDay) => perDay >= 1 ? "elite" : perDay >= 1.0 / 7 ? "high" : perDay >= 1.0 / 30 ? "medium" : "low";

    private static string LeadRating(double? hours) => hours switch
    {
        null => "n/a",
        < 24 => "elite",
        < 168 => "high",
        < 720 => "medium",
        _ => "low"
    };

    private static string FailureRating(double? percent) => percent switch
    {
        null => "n/a",
        <= 5 => "elite",
        <= 10 => "high",
        <= 15 => "medium",
        _ => "low"
    };

    private static string RestoreRating(double? hours) => hours switch
    {
        null => "n/a",
        < 1 => "elite",
        < 24 => "high",
        < 168 => "medium",
        _ => "low"
    };
}
