using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Tests.Infrastructure;

/// <summary>A fixed, realistic change: issue #1941 -> PR #1950 -> build 2.4.2664 -> RC -> Octopus release -> tdd/uat/prod.</summary>
public static class TwinStateFixtures
{
    public const string Sha = "9d4a43165eb9d090ab0847bbb4cc6398f1d29699";
    public const string Version = "2.4.2664";
    public static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public static RepositorySnapshot Repository() => new()
    {
        Name = "cmdemo1-workorders", FullName = "clearmeasure-aisf-sample-apps/cmdemo1-workorders", Url = "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders",
        DefaultBranch = "master", PipelineId = "app-ui", LatestCommitSha = Sha, LatestCommitMessage = "Add DST tests (#1950)", LatestCommitAt = T0.AddMinutes(30), UpdatedAt = T0
    };

    public static WorkItemSnapshot WorkItem() => new()
    {
        Id = "cmdemo1-workorders#1941", Repository = "cmdemo1-workorders", Number = 1941, Title = "Add DST and month-boundary tests",
        Url = "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/1941", State = "open", Column = "In Progress",
        WorkerKind = "bot", Agent = "cm-ai-ops-bot", CreatedAt = T0, ReleaseCandidateVersion = Version, UpdatedAt = T0
    };

    public static SourceChangeSnapshot PullRequest() => new()
    {
        Id = "cmdemo1-workorders#1950", Repository = "cmdemo1-workorders", Number = 1950, Title = "Add DST tests (#1941)", State = "merged",
        HeadBranch = "cursor/dst-tests-1941", BaseBranch = "master", Author = "cm-ai-ops-bot", IsBot = true, PipelineId = "app-ui",
        LinkedIssueNumber = 1941, MergeCommitSha = Sha, CreatedAt = T0.AddMinutes(5), UpdatedAt = T0.AddMinutes(30), MergedAt = T0.AddMinutes(30),
        Url = "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pull/1950"
    };

    public static PipelineRunSnapshot Build() => new()
    {
        Id = "cmdemo1-workorders:44863242282", RunId = 44863242282, RunNumber = 2664, Repository = "cmdemo1-workorders", PipelineId = "app-ui",
        WorkflowName = "Build", DisplayTitle = Version, Version = Version, EventName = "push", Branch = "master", HeadSha = Sha,
        Status = "completed", Conclusion = "success", CreatedAt = T0.AddMinutes(31), UpdatedAt = T0.AddMinutes(41),
        Url = "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders/actions/runs/44863242282"
    };

    public static ArtifactPackageSnapshot Artifact() => new()
    {
        Id = $"OctopusPackage:ui:{Version}", Kind = "OctopusPackage", Name = "ui", Version = Version, RegistryOrFeed = "octopus-builtin-feed",
        CommitSha = Sha, PipelineId = "app-ui", PublishedAt = T0.AddMinutes(42)
    };

    public static ReleaseSnapshot Release() => new()
    {
        ReleaseId = "Releases-52228", ProjectSlug = "cmdemo1-ui", DeployableName = "ui", PipelineId = "app-ui", Version = Version,
        GitCommitSha = Sha, CommittedAt = T0.AddMinutes(30), CreatedAt = T0.AddMinutes(43), ReleaseUrl = "https://clearmeasure.octopus.app/app#/Spaces-355/releases/52228"
    };

    public static DeliveryExecutionSnapshot Delivery(string stage, string state, int startMinute, int? finishMinute) => new()
    {
        DeploymentId = $"Deployments-{stage}", TaskId = $"ServerTasks-{stage}", ProjectSlug = "cmdemo1-ui", DeployableName = "ui", PipelineId = "app-ui",
        StageName = stage, ReleaseVersion = Version, State = state, QueuedAt = T0.AddMinutes(startMinute - 1), StartedAt = T0.AddMinutes(startMinute),
        FinishedAt = finishMinute is null ? null : T0.AddMinutes(finishMinute.Value), GitCommitSha = Sha, CommittedAt = T0.AddMinutes(30),
        LeadTimeHours = finishMinute is null ? null : Math.Round((finishMinute.Value - 30) / 60.0, 2), TaskUrl = $"https://clearmeasure.octopus.app/app#/Spaces-355/tasks/ServerTasks-{stage}"
    };

    public static AzureResourceSnapshot Resource(string name) => new()
    {
        ResourceId = $"/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/{name}", Name = name,
        Type = "Microsoft.App/containerApps", ResourceGroup = "rg-cmdemo1-nonprod", Location = "southcentralus", StageName = "tdd",
        ProvisioningState = "Succeeded", ObservedAt = T0
    };

    public static WorkloadRevisionSnapshot Workload(string stage, string version) => new()
    {
        Id = $"{stage}:ui", StageName = stage, DeployableName = "ui", ResourceGroup = "rg-cmdemo1-nonprod", ResourceName = $"ca-cmdemo1-{stage}-ui",
        HostingBoundary = "cae-cmdemo1-tdd", RunningVersion = version, PinnedVersion = version, HealthState = "Healthy", ObservedAt = T0
    };

    public static DriftFinding Drift() => new()
    {
        DriftKey = "pending-promotion:prod:ui", Category = DriftCategory.PendingPromotion, Severity = DriftSeverity.Info, StageName = "prod",
        DeployableName = "ui", ExpectedValue = Version, ObservedValue = "2.4.2650", Summary = "ui in prod trails tdd", DetectedAt = T0
    };

    public static WebhookRegistrationStatus Hook(string repo) => new()
    {
        Id = $"github:{repo}", Source = "github", Target = repo, EndpointUrl = "https://twin/hooks/github", IsActive = true, Detail = "active", CheckedAt = T0
    };

    public static HealthProbeSnapshot Probe(string stage, string version, int latency) => new()
    {
        TargetKey = $"{stage}:ui", StageName = stage, DeployableName = "ui", Url = $"https://ui-{stage}", HttpStatusCode = 200, IsHealthy = true,
        LatencyMs = latency, ReportedVersion = version, CheckedAt = T0
    };
}
