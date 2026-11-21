namespace DigitalTwin.Core.Hierarchy;

public sealed record PipelineDefinition(
    string Id,
    string Name,
    string Kind,
    string Repository,
    string? DeployableName,
    string? OctopusProjectSlug,
    bool IsRetired,
    bool IsTwinObserver,
    string Description);

public sealed record DeploymentStageDefinition(
    string Name,
    string Tier,
    string HostingBoundary,
    string ResourceGroup,
    int Order,
    bool AutoDeploy,
    bool HasAcceptanceTests,
    bool RequiresRestorePoint,
    bool IsDeclaredOnly = false);

public sealed record HostingBoundaryDefinition(
    string Name,
    string Kind,
    string ResourceGroup,
    string Location,
    string[] HostedStages);

public static class Cmdemo1PlatformCatalog
{
    public const string PlatformName = "Clear Measure Demo Platform";
    public const string SystemSlug = "cmdemo1";
    public const string SystemName = "Clear Measure work orders demo 1";
    public const string GitHubOrg = "clearmeasure-aisf-sample-apps";
    public const string SystemRepository = "cmdemo1-system";
    public const string TwinRepository = "cmdemo1-digitaltwin";
    public const string SubscriptionId = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69";
    public const string TenantId = "40645332-2e20-4bca-882c-b2706f93ce44";
    public const string NonprodResourceGroup = "rg-cmdemo1-nonprod";
    public const string ProdResourceGroup = "rg-cmdemo1-prod";
    public const string AcrName = "acrcmdemo1chu4gq";
    public const string AcrLoginServer = "acrcmdemo1chu4gq.azurecr.io";
    public const string OctopusUrl = "https://clearmeasure.octopus.app";
    public const string OctopusSpaceId = "Spaces-355";
    public const string OctopusSpaceSlug = "cmdemo1-demo";
    public const int WorkItemsProjectNumber = 679;
    public const string WorkItemsProjectNodeId = "PVT_kwDOD_ESUc4BliGL";

    public static IReadOnlyList<PipelineDefinition> Pipelines { get; } =
    [
        new(
            Id: "app-ui",
            Name: "Work Orders UI",
            Kind: "BusinessApp",
            Repository: "cmdemo1-workorders",
            DeployableName: "ui",
            OctopusProjectSlug: "cmdemo1-ui",
            IsRetired: false,
            IsTwinObserver: false,
            Description: "Primary .NET work-orders application deployed as Azure Container Apps with Azure SQL and Playwright acceptance tests."),
        new(
            Id: "app-dashboard",
            Name: "Health Dashboard",
            Kind: "BusinessApp",
            Repository: "cmdemo1-dashboard",
            DeployableName: "dashboard",
            OctopusProjectSlug: "cmdemo1-dashboard",
            IsRetired: false,
            IsTwinObserver: false,
            Description: "Blazor WebAssembly system health dashboard deployed to Azure Static Web Apps."),
        new(
            Id: "system-gitops",
            Name: "System GitOps & Infra",
            Kind: "GitOpsInfra",
            Repository: "cmdemo1-system",
            DeployableName: "system",
            OctopusProjectSlug: "cmdemo1-system",
            IsRetired: false,
            IsTwinObserver: false,
            Description: "Desired state declarations (system.json, Bicep stacks, Octopus Terraform, status feeds, and operational runbooks)."),
        new(
            Id: "measurement-scorecard",
            Name: "Scorecard & Measurement",
            Kind: "Measurement",
            Repository: "cmdemo1-scorecard-site",
            DeployableName: "scorecard",
            OctopusProjectSlug: "cmdemo1-scorecard-space",
            IsRetired: false,
            IsTwinObserver: false,
            Description: "Nightly Clear Measure Intelligence Scorecard publication to GitHub Pages and prod blob site (runtime scorecard app retired)."),
        new(
            Id: "factory-agentic",
            Name: "Software Factory & Agents",
            Kind: "SoftwareFactory",
            Repository: "cmdemo1-fworker",
            DeployableName: "fworker",
            OctopusProjectSlug: "cmdemo1-fworker",
            IsRetired: true,
            IsTwinObserver: false,
            Description: "Autonomous bot/agent delivery pipeline across Project 679 and PRs (legacy aisf/fworker/app2-ui runtimes retired)."),
        new(
            Id: "twin-observer",
            Name: "Digital Twin Observer",
            Kind: "PlatformObserver",
            Repository: "cmdemo1-digitaltwin",
            DeployableName: null,
            OctopusProjectSlug: null,
            IsRetired: false,
            IsTwinObserver: true,
            Description: "Platform-level digital twin sitting one level above cmdemo1 to observe and reconcile without becoming a cmdemo1 deployable.")
    ];

    public static IReadOnlyList<DeploymentStageDefinition> Stages { get; } =
    [
        new("tdd", "nonprod", "cae-cmdemo1-tdd", NonprodResourceGroup, 1, AutoDeploy: true, HasAcceptanceTests: true, RequiresRestorePoint: false),
        new("uat", "nonprod", "cae-cmdemo1-tdd", NonprodResourceGroup, 2, AutoDeploy: false, HasAcceptanceTests: false, RequiresRestorePoint: false),
        new("prod", "prod", "cae-cmdemo1-prod-ekdi", ProdResourceGroup, 3, AutoDeploy: false, HasAcceptanceTests: false, RequiresRestorePoint: true),
        new("pool", "nonprod", "cae-cmdemo1-pool", NonprodResourceGroup, 4, AutoDeploy: false, HasAcceptanceTests: true, RequiresRestorePoint: false, IsDeclaredOnly: true)
    ];

    public static IReadOnlyList<HostingBoundaryDefinition> HostingBoundaries { get; } =
    [
        new("cae-cmdemo1-tdd", "ContainerAppsEnvironment", NonprodResourceGroup, "southcentralus", ["tdd", "uat"]),
        new("cae-cmdemo1-prod-ekdi", "ContainerAppsEnvironment", ProdResourceGroup, "southcentralus", ["prod"]),
        new("swa-centralus", "StaticWebAppsEdge", "rg-cmdemo1-nonprod,rg-cmdemo1-prod", "centralus", ["tdd", "uat", "prod"]),
        new("cae-cmdemo1-twin", "ContainerAppsEnvironment (Platform Twin)", NonprodResourceGroup, "southcentralus", ["platform-twin"])
    ];

    public static IReadOnlyList<string> ObservedRepositories { get; } =
    [
        "cmdemo1-system",
        "cmdemo1-workorders",
        "cmdemo1-dashboard",
        "cmdemo1-aisf",
        "cmdemo1-fworker",
        "cmdemo1-app2-ui",
        "cmdemo1-scorecard",
        "cmdemo1-scorecard-site",
        "cmdemo1-video",
        "cmdemo1-digitaltwin"
    ];

    public static string ResolvePipelineIdFromRepository(string? repositoryName)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            return "system-gitops";
        }

        var shortName = repositoryName.Contains('/', StringComparison.Ordinal)
            ? repositoryName.Split('/', 2)[1]
            : repositoryName;

        return shortName.ToLowerInvariant() switch
        {
            "cmdemo1-workorders" => "app-ui",
            "cmdemo1-dashboard" => "app-dashboard",
            "cmdemo1-system" => "system-gitops",
            "cmdemo1-scorecard" or "cmdemo1-scorecard-site" => "measurement-scorecard",
            "cmdemo1-fworker" or "cmdemo1-aisf" or "cmdemo1-app2-ui" or "cmdemo1-video" => "factory-agentic",
            "cmdemo1-digitaltwin" => "twin-observer",
            _ => "system-gitops"
        };
    }

    public static string ResolvePipelineIdFromOctopusProject(string? projectSlugOrName)
    {
        if (string.IsNullOrWhiteSpace(projectSlugOrName))
        {
            return "system-gitops";
        }

        return projectSlugOrName.ToLowerInvariant() switch
        {
            "cmdemo1-ui" => "app-ui",
            "cmdemo1-dashboard" => "app-dashboard",
            "cmdemo1-system" => "system-gitops",
            "cmdemo1-scorecard" or "cmdemo1-scorecard-space" => "measurement-scorecard",
            "cmdemo1-aisf" or "cmdemo1-fworker" or "cmdemo1-fworker-runtime" => "factory-agentic",
            _ => "system-gitops"
        };
    }

    public static string? ResolveDeployableFromRepository(string? repositoryName)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            return null;
        }

        var shortName = repositoryName.Contains('/', StringComparison.Ordinal)
            ? repositoryName.Split('/', 2)[1]
            : repositoryName;

        return shortName.ToLowerInvariant() switch
        {
            "cmdemo1-workorders" => "ui",
            "cmdemo1-dashboard" => "dashboard",
            "cmdemo1-system" => "system",
            "cmdemo1-scorecard" or "cmdemo1-scorecard-site" => "scorecard",
            "cmdemo1-aisf" => "aisf",
            "cmdemo1-fworker" => "fworker",
            _ => null
        };
    }

    public static string? ResolveDeployableFromOctopusProject(string? projectSlug)
    {
        if (string.IsNullOrWhiteSpace(projectSlug))
        {
            return null;
        }

        return projectSlug.ToLowerInvariant() switch
        {
            "cmdemo1-ui" => "ui",
            "cmdemo1-dashboard" => "dashboard",
            "cmdemo1-system" => "system",
            "cmdemo1-scorecard" or "cmdemo1-scorecard-space" => "scorecard",
            "cmdemo1-aisf" => "aisf",
            "cmdemo1-fworker" or "cmdemo1-fworker-runtime" => "fworker",
            _ => null
        };
    }

    public static string? InferStageFromAzureResourceName(string? resourceName, string? resourceGroup)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        var lower = resourceName.ToLowerInvariant();
        if (lower.Contains("-twin", StringComparison.Ordinal) || lower.Contains("twin", StringComparison.Ordinal))
        {
            return "twin";
        }
        if (lower.Contains("-tdd", StringComparison.Ordinal) || lower.Contains("cmdemo1tdd", StringComparison.Ordinal))
        {
            return "tdd";
        }
        if (lower.Contains("-uat", StringComparison.Ordinal) || lower.Contains("cmdemo1uat", StringComparison.Ordinal))
        {
            return "uat";
        }
        if (lower.Contains("-prod", StringComparison.Ordinal) || lower.Contains("cmdemo1prod", StringComparison.Ordinal) ||
            string.Equals(resourceGroup, ProdResourceGroup, StringComparison.OrdinalIgnoreCase))
        {
            return "prod";
        }
        if (lower.Contains("-pool", StringComparison.Ordinal) || lower.Contains("cmdemo1pool", StringComparison.Ordinal))
        {
            return "pool";
        }

        return "shared";
    }
}
