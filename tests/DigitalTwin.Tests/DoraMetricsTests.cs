using DigitalTwin.Domain.Measurement;
using DigitalTwin.Domain.ReleaseAndDelivery;
using Xunit;

namespace DigitalTwin.Tests;

public sealed class DoraMetricsTests
{
    [Fact]
    public void CalculatesFiveDoraMetricsAndCountsGuidedFailuresAsFailed()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var deployments = new List<DeliveryExecutionSnapshot>
        {
            // Successful prod deploy (lead time 2h)
            new()
            {
                DeploymentId = "Deployments-1",
                ProjectSlug = "cmdemo1-ui",
                DeployableName = "ui",
                StageName = "prod",
                ChannelName = "Default",
                State = "succeeded",
                HasGuidedFailure = false,
                QueuedAt = now.AddDays(-3),
                FinishedAt = now.AddDays(-3).AddMinutes(10),
                LeadTimeHours = 2.0
            },
            // Failed prod deploy at T-2d
            new()
            {
                DeploymentId = "Deployments-2",
                ProjectSlug = "cmdemo1-ui",
                DeployableName = "ui",
                StageName = "prod",
                ChannelName = "Default",
                State = "failed",
                HasGuidedFailure = false,
                QueuedAt = now.AddDays(-2),
                FinishedAt = now.AddDays(-2)
            },
            // Recovery prod deploy at T-2d + 1.5h (succeeded with NO guided failure)
            new()
            {
                DeploymentId = "Deployments-3",
                ProjectSlug = "cmdemo1-ui",
                DeployableName = "ui",
                StageName = "prod",
                ChannelName = "Default",
                State = "succeeded",
                HasGuidedFailure = false,
                QueuedAt = now.AddDays(-2).AddHours(1.4),
                FinishedAt = now.AddDays(-2).AddHours(1.5),
                LeadTimeHours = 4.0
            },
            // Task succeeded, BUT HasGuidedFailure=true -> DORA counts as failure!
            new()
            {
                DeploymentId = "Deployments-4",
                ProjectSlug = "cmdemo1-dashboard",
                DeployableName = "dashboard",
                StageName = "prod",
                ChannelName = "Default",
                State = "succeeded",
                HasGuidedFailure = true,
                QueuedAt = now.AddDays(-1),
                FinishedAt = now.AddDays(-1),
                LeadTimeHours = 1.0
            },
            // Pull requests channel deployment should be excluded from prod DORA metrics
            new()
            {
                DeploymentId = "Deployments-5",
                ProjectSlug = "cmdemo1-ui",
                DeployableName = "ui",
                StageName = "prod",
                ChannelName = "Pull requests",
                State = "failed",
                QueuedAt = now.AddDays(-1),
                FinishedAt = now.AddDays(-1)
            }
        };

        var metrics = DoraMetricsCalculator.Calculate(deployments, workReworkRate: 0.052, now, windowDays: 7, scopeStage: "prod");

        Assert.Equal(4, metrics.TotalDeployments);
        Assert.Equal(2, metrics.SuccessfulDeployments);
        Assert.Equal(2, metrics.FailedDeployments);
        Assert.Equal(50.0, metrics.ChangeFailRatePercent);
        Assert.Equal(3.0, metrics.MedianLeadTimeHours);
        Assert.Equal(1.5, metrics.FailedDeploymentRecoveryHours);
        Assert.Equal(5.2, metrics.ReworkRatePercent);
    }
}
