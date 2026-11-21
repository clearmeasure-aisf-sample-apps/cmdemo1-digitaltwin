using DigitalTwin.Domain.ReliabilityAndDrift;
using DigitalTwin.Domain.RuntimeAndPlatform;
using DigitalTwin.Domain.SourceAndChange;
using Xunit;

namespace DigitalTwin.Tests;

public sealed class DriftEvaluatorTests
{
    private const string SampleSystemJson = """
        {
          "deployables": [
            { "name": "ui", "hosting": "containerapp" },
            { "name": "dashboard", "hosting": "staticwebapp" },
            { "name": "aisf", "hosting": "containerapp", "retired": true }
          ]
        }
        """;

    [Fact]
    public void CleanStateProducesNoCriticalDrift()
    {
        var now = DateTimeOffset.UtcNow;
        var desired = new DesiredSystemConfigSnapshot
        {
            SystemJson = SampleSystemJson,
            PinnedVersionsJson = """
                {
                  "tdd": { "ui": "2.4.2664", "dashboard": "1.0.408" },
                  "uat": { "ui": "2.4.2664", "dashboard": "1.0.408" },
                  "prod": { "ui": "2.4.2664", "dashboard": "1.0.408" }
                }
                """
        };

        var workloads = new List<WorkloadRevisionSnapshot>
        {
            new() { Id = "tdd:ui", StageName = "tdd", DeployableName = "ui", HostingType = "containerapp", ResourceName = "ca-cmdemo1-tdd-ui", RunningVersion = "2.4.2664", ProvisioningState = "Provisioned", RunningState = "Running", HealthState = "Healthy" },
            new() { Id = "tdd:dashboard", StageName = "tdd", DeployableName = "dashboard", HostingType = "staticwebapp", ResourceName = "swa-cmdemo1-tdd-dashboard", RunningVersion = "1.0.408", HealthState = "Healthy" },
            new() { Id = "uat:ui", StageName = "uat", DeployableName = "ui", HostingType = "containerapp", ResourceName = "ca-cmdemo1-uat-ui-fccv", RunningVersion = "2.4.2664", ProvisioningState = "Provisioned", RunningState = "Running", HealthState = "Healthy" },
            new() { Id = "uat:dashboard", StageName = "uat", DeployableName = "dashboard", HostingType = "staticwebapp", ResourceName = "swa-cmdemo1-uat-dashboard", RunningVersion = "1.0.408", HealthState = "Healthy" },
            new() { Id = "prod:ui", StageName = "prod", DeployableName = "ui", HostingType = "containerapp", ResourceName = "ca-cmdemo1-prod-ui-bhj5", RunningVersion = "2.4.2664", ProvisioningState = "Provisioned", RunningState = "Running", HealthState = "Healthy" },
            new() { Id = "prod:dashboard", StageName = "prod", DeployableName = "dashboard", HostingType = "staticwebapp", ResourceName = "swa-cmdemo1-prod-dashboard", RunningVersion = "1.0.408", HealthState = "Healthy" }
        };

        var probes = workloads.Select(w => new HealthProbeSnapshot
        {
            TargetKey = w.Id,
            StageName = w.StageName,
            DeployableName = w.DeployableName,
            IsHealthy = true,
            HttpStatusCode = 200
        }).ToList();

        var repos = new List<RepositorySnapshot>
        {
            new() { Name = "cmdemo1-workorders", WebhookRegistered = true },
            new() { Name = "cmdemo1-app2-ui", IsRetired = true, WebhookRegistered = false }
        };

        var findings = DriftEvaluator.Evaluate(desired, workloads, probes, [], repos, now);

        Assert.Empty(findings);
    }

    [Fact]
    public void DetectsVersionMismatchUnhealthyEndpointDegradedRevisionAndRetiredLeftovers()
    {
        var now = DateTimeOffset.UtcNow;
        var desired = new DesiredSystemConfigSnapshot
        {
            SystemJson = SampleSystemJson,
            PinnedVersionsJson = """
                {
                  "tdd": { "ui": "2.4.2665", "dashboard": "1.0.408" },
                  "uat": { "ui": "2.4.2664", "dashboard": "1.0.408" },
                  "prod": { "ui": "2.4.2661", "dashboard": "1.0.405" }
                }
                """
        };

        var workloads = new List<WorkloadRevisionSnapshot>
        {
            new() { Id = "tdd:ui", StageName = "tdd", DeployableName = "ui", HostingType = "containerapp", ResourceName = "ca-cmdemo1-tdd-ui", RunningVersion = "2.4.2664", ActiveRevisionName = "ca-cmdemo1-tdd-ui--0000399", ProvisioningState = "Failed", RunningState = "Degraded", HealthState = "Unhealthy" },
            new() { Id = "tdd:dashboard", StageName = "tdd", DeployableName = "dashboard", HostingType = "staticwebapp", ResourceName = "swa-cmdemo1-tdd-dashboard", RunningVersion = "1.0.408" },
            new() { Id = "uat:ui", StageName = "uat", DeployableName = "ui", HostingType = "containerapp", ResourceName = "ca-cmdemo1-uat-ui-fccv", RunningVersion = "2.4.2664", HealthState = "Healthy" },
            new() { Id = "uat:dashboard", StageName = "uat", DeployableName = "dashboard", HostingType = "staticwebapp", ResourceName = "swa-cmdemo1-uat-dashboard", RunningVersion = "1.0.408" },
            new() { Id = "prod:ui", StageName = "prod", DeployableName = "ui", HostingType = "containerapp", ResourceName = "ca-cmdemo1-prod-ui-bhj5", RunningVersion = "2.4.2661", HealthState = "Healthy" },
            new() { Id = "prod:dashboard", StageName = "prod", DeployableName = "dashboard", HostingType = "staticwebapp", ResourceName = "swa-cmdemo1-prod-dashboard", RunningVersion = "1.0.405" }
        };

        var probes = new List<HealthProbeSnapshot>
        {
            new() { TargetKey = "tdd:ui", StageName = "tdd", DeployableName = "ui", IsHealthy = false, HttpStatusCode = 503, Url = "https://tdd", HealthPath = "/_healthcheck" }
        };

        var azureResources = new List<AzureResourceSnapshot>
        {
            new() { ResourceId = "/sub/rg/ca-cmdemo1-prod-aisf-bhj5", Name = "ca-cmdemo1-prod-aisf-bhj5", Type = "Microsoft.App/containerApps", ResourceGroup = "rg-cmdemo1-prod", StageName = "prod" }
        };

        var findings = DriftEvaluator.Evaluate(desired, workloads, probes, azureResources, [], now);

        Assert.Contains(findings, f => f.Category == DriftCategory.VersionMismatch && f.StageName == "tdd" && f.ExpectedValue == "2.4.2665");
        Assert.Contains(findings, f => f.Category == DriftCategory.RevisionDegraded && f.StageName == "tdd");
        Assert.Contains(findings, f => f.Category == DriftCategory.UnhealthyEndpoint && f.StageName == "tdd");
        Assert.Contains(findings, f => f.Category == DriftCategory.RetiredResourcePresent && f.ResourceId == "/sub/rg/ca-cmdemo1-prod-aisf-bhj5");
        Assert.Contains(findings, f => f.Category == DriftCategory.PendingPromotion && f.StageName == "prod");
    }
}
