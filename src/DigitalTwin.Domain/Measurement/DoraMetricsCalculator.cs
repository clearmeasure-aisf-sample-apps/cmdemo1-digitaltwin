using DigitalTwin.Domain.ReleaseAndDelivery;

namespace DigitalTwin.Domain.Measurement;

public sealed record DoraMetricsSnapshot(
    int WindowDays,
    string ScopeStage,
    int TotalDeployments,
    int SuccessfulDeployments,
    int FailedDeployments,
    double DeploymentsPerDay,
    double? MedianLeadTimeHours,
    double? P85LeadTimeHours,
    double ChangeFailRatePercent,
    double? FailedDeploymentRecoveryHours,
    double? ReworkRatePercent,
    IReadOnlyDictionary<string, int> DeploymentsByStage,
    IReadOnlyDictionary<string, int> DeploymentsByDeployable);

public static class DoraMetricsCalculator
{
    public static DoraMetricsSnapshot Calculate(
        IReadOnlyList<DeliveryExecutionSnapshot> deployments,
        double? workReworkRate,
        DateTimeOffset now,
        int windowDays = 7,
        string scopeStage = "prod")
    {
        var since = now.AddDays(-windowDays);
        var defaultChannelCompleted = deployments
            .Where(d => d.ChannelName.Equals("Default", StringComparison.OrdinalIgnoreCase)
                     && d.FinishedAt is not null
                     && d.FinishedAt >= since
                     && d.State is "succeeded" or "failed")
            .OrderBy(d => d.FinishedAt)
            .ToList();

        var scoped = string.Equals(scopeStage, "all", StringComparison.OrdinalIgnoreCase)
            ? defaultChannelCompleted
            : defaultChannelCompleted.Where(d => d.StageName.Equals(scopeStage, StringComparison.OrdinalIgnoreCase)).ToList();

        // DORA + Octopus Insights rule: a deployment with a guided failure counts as failed even if task ended Success
        static bool IsFailure(DeliveryExecutionSnapshot d)
            => d.State == "failed" || d.HasGuidedFailure;

        var total = scoped.Count;
        var failed = scoped.Count(IsFailure);
        var succeeded = total - failed;
        var freqPerDay = windowDays > 0 ? Math.Round((double)total / windowDays, 2) : 0;
        var cfr = total > 0 ? Math.Round((double)failed / total * 100.0, 1) : 0.0;

        var leadTimes = scoped
            .Where(d => !IsFailure(d) && d.LeadTimeHours is >= 0)
            .Select(d => d.LeadTimeHours!.Value)
            .OrderBy(x => x)
            .ToList();

        var recoveryTimes = ComputeRecoveryHours(defaultChannelCompleted);

        var byStage = defaultChannelCompleted
            .GroupBy(d => d.StageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var byDeployable = scoped
            .Where(d => !string.IsNullOrWhiteSpace(d.DeployableName))
            .GroupBy(d => d.DeployableName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new DoraMetricsSnapshot(
            WindowDays: windowDays,
            ScopeStage: scopeStage,
            TotalDeployments: total,
            SuccessfulDeployments: succeeded,
            FailedDeployments: failed,
            DeploymentsPerDay: freqPerDay,
            MedianLeadTimeHours: Percentile(leadTimes, 0.5),
            P85LeadTimeHours: Percentile(leadTimes, 0.85),
            ChangeFailRatePercent: cfr,
            FailedDeploymentRecoveryHours: Percentile(recoveryTimes, 0.5),
            ReworkRatePercent: workReworkRate is null ? null : Math.Round(workReworkRate.Value * 100.0, 1),
            DeploymentsByStage: byStage,
            DeploymentsByDeployable: byDeployable);
    }

    private static List<double> ComputeRecoveryHours(IReadOnlyList<DeliveryExecutionSnapshot> ordered)
    {
        var recoveries = new List<double>();
        var openFailures = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in ordered)
        {
            if (d.FinishedAt is null)
            {
                continue;
            }
            var key = $"{d.ProjectSlug}|{d.StageName}";
            var isFailure = d.State == "failed" || d.HasGuidedFailure;

            if (isFailure)
            {
                openFailures.TryAdd(key, d.FinishedAt.Value);
            }
            else if (openFailures.Remove(key, out var failedAt) && d.FinishedAt.Value >= failedAt)
            {
                recoveries.Add(Math.Round((d.FinishedAt.Value - failedAt).TotalHours, 2));
            }
        }

        return [.. recoveries.OrderBy(x => x)];
    }

    public static double? Percentile(IReadOnlyList<double> sortedValues, double p)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }
        var rank = (sortedValues.Count - 1) * p;
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        var val = sortedValues[low] + (sortedValues[high] - sortedValues[low]) * (rank - low);
        return Math.Round(val, 2);
    }
}
