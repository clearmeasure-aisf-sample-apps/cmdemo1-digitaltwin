using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalTwin.Core.Hierarchy;

/// <summary>
/// Everything the twin needs to know about one client's platform: its GitHub organization and repositories, Octopus space and projects,
/// Azure subscription and resource groups, pipelines, stages and hosting boundaries. Loaded from JSON (<c>Twin:Platform:Json</c> or
/// <c>Twin:Platform:File</c>); <see cref="Cmdemo1"/> is the built-in default and docs/platforms/cmdemo1.platform.json its serialized form.
/// </summary>
public sealed class PlatformDefinition
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string PlatformName { get; init; } = "";
    public string SystemSlug { get; init; } = "";
    public string SystemName { get; init; } = "";

    /// <summary>The twin's own name in health, user agents and the Octopus subscription; defaults to "{systemSlug}-digitaltwin".</summary>
    public string? TwinName { get; init; }

    public GitHubPlatform GitHub { get; init; } = new();
    public OctopusPlatform Octopus { get; init; } = new();
    public AzurePlatform Azure { get; init; } = new();

    /// <summary>Pipeline for anything not mapped below (the system's GitOps pipeline on cmdemo1).</summary>
    public string DefaultPipelineId { get; init; } = "";

    public List<PipelineDefinition> Pipelines { get; init; } = [];
    public List<DeploymentStageDefinition> Stages { get; init; } = [];
    public List<HostingBoundaryDefinition> HostingBoundaries { get; init; } = [];

    /// <summary>Short repository name (lower case) to pipeline id, including repositories that share a pipeline.</summary>
    public Dictionary<string, string> RepositoryPipelines { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Short repository name to deployable, for repositories that ship one.</summary>
    public Dictionary<string, string> RepositoryDeployables { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Octopus project slug or name to pipeline id.</summary>
    public Dictionary<string, string> OctopusProjectPipelines { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Octopus project slug or name to deployable.</summary>
    public Dictionary<string, string> OctopusProjectDeployables { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>First match wins: a resource whose name contains any token, or that sits in any listed resource group, belongs to the stage.</summary>
    public List<StageInferenceRule> AzureStageRules { get; init; } = [];

    /// <summary>Stage for Azure resources no rule matches.</summary>
    public string DefaultAzureStage { get; init; } = "shared";

    /// <summary>The active (not declared-only) stages in delivery order: two or three, the last one is production.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> DeliveryStageNames => Stages.Where(s => !s.IsDeclaredOnly).OrderBy(s => s.Order).Select(s => s.Name).ToList();

    [JsonIgnore]
    public string FirstStage => DeliveryStageNames[0];

    [JsonIgnore]
    public string ProductionStage => DeliveryStageNames[^1];

    [JsonIgnore]
    public string TwinServiceName => string.IsNullOrWhiteSpace(TwinName) ? $"{SystemSlug}-digitaltwin" : TwinName;

    public static PlatformDefinition FromJson(string json)
    {
        var definition = JsonSerializer.Deserialize<PlatformDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException("The platform definition is empty.");
        definition = definition.WithCaseInsensitiveMaps();
        definition.Validate();
        return definition;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Throws with every problem found, so a misconfigured platform fails at startup rather than observing the wrong thing.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        void Require(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{name} is required");
            }
        }

        Require(PlatformName, "platformName");
        Require(SystemSlug, "systemSlug");
        Require(SystemName, "systemName");
        Require(GitHub.Org, "gitHub.org");
        Require(GitHub.SystemRepository, "gitHub.systemRepository");
        Require(GitHub.TwinRepository, "gitHub.twinRepository");
        Require(Octopus.Url, "octopus.url");
        Require(Octopus.SpaceId, "octopus.spaceId");
        Require(Azure.SubscriptionId, "azure.subscriptionId");
        Require(Azure.NonprodResourceGroup, "azure.nonprodResourceGroup");
        Require(Azure.ProdResourceGroup, "azure.prodResourceGroup");
        Require(DefaultPipelineId, "defaultPipelineId");
        if (GitHub.ObservedRepositories.Count == 0)
        {
            errors.Add("gitHub.observedRepositories needs at least one repository");
        }
        var delivery = Stages.Count(s => !s.IsDeclaredOnly);
        if (delivery is < 2 or > 3)
        {
            errors.Add($"stages needs two or three active (not declared-only) stages in delivery order, found {delivery}");
        }

        var pipelineIds = Pipelines.Select(p => p.Id).ToList();
        errors.AddRange(pipelineIds.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"pipeline id '{g.Key}' is duplicated"));
        if (!Pipelines.Any(p => p.IsTwinObserver))
        {
            errors.Add("pipelines needs one twin observer pipeline (isTwinObserver: true)");
        }
        var known = pipelineIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (DefaultPipelineId.Length > 0 && !known.Contains(DefaultPipelineId))
        {
            errors.Add($"defaultPipelineId '{DefaultPipelineId}' is not a pipeline");
        }
        errors.AddRange(RepositoryPipelines.Concat(OctopusProjectPipelines).Where(kv => !known.Contains(kv.Value))
            .Select(kv => $"'{kv.Key}' maps to unknown pipeline '{kv.Value}'"));
        errors.AddRange(Stages.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"stage '{g.Key}' is duplicated"));

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid platform definition: " + string.Join("; ", errors));
        }
    }

    public string ResolvePipelineIdFromRepository(string? repositoryName)
        => ShortName(repositoryName) is { } name && RepositoryPipelines.TryGetValue(name, out var id) ? id : DefaultPipelineId;

    public string ResolvePipelineIdFromOctopusProject(string? projectSlugOrName)
        => !string.IsNullOrWhiteSpace(projectSlugOrName) && OctopusProjectPipelines.TryGetValue(projectSlugOrName, out var id) ? id : DefaultPipelineId;

    public string? ResolveDeployableFromRepository(string? repositoryName)
        => ShortName(repositoryName) is { } name && RepositoryDeployables.TryGetValue(name, out var deployable) ? deployable : null;

    public string? ResolveDeployableFromOctopusProject(string? projectSlug)
        => !string.IsNullOrWhiteSpace(projectSlug) && OctopusProjectDeployables.TryGetValue(projectSlug, out var deployable) ? deployable : null;

    public string? InferStageFromAzureResourceName(string? resourceName, string? resourceGroup)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        foreach (var rule in AzureStageRules)
        {
            if (rule.NameContains.Any(token => resourceName.Contains(token, StringComparison.OrdinalIgnoreCase))
                || rule.ResourceGroups.Any(rg => string.Equals(rg, resourceGroup, StringComparison.OrdinalIgnoreCase)))
            {
                return rule.Stage;
            }
        }

        return DefaultAzureStage;
    }

    private static string? ShortName(string? repositoryName)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            return null;
        }

        return repositoryName.Contains('/', StringComparison.Ordinal) ? repositoryName.Split('/', 2)[1] : repositoryName;
    }

    private PlatformDefinition WithCaseInsensitiveMaps() => new()
    {
        PlatformName = PlatformName,
        SystemSlug = SystemSlug,
        SystemName = SystemName,
        TwinName = TwinName,
        GitHub = GitHub,
        Octopus = Octopus,
        Azure = Azure,
        DefaultPipelineId = DefaultPipelineId,
        Pipelines = Pipelines,
        Stages = Stages,
        HostingBoundaries = HostingBoundaries,
        RepositoryPipelines = new(RepositoryPipelines, StringComparer.OrdinalIgnoreCase),
        RepositoryDeployables = new(RepositoryDeployables, StringComparer.OrdinalIgnoreCase),
        OctopusProjectPipelines = new(OctopusProjectPipelines, StringComparer.OrdinalIgnoreCase),
        OctopusProjectDeployables = new(OctopusProjectDeployables, StringComparer.OrdinalIgnoreCase),
        AzureStageRules = AzureStageRules,
        DefaultAzureStage = DefaultAzureStage
    };

    /// <summary>The Clear Measure demo platform (cmdemo1), observed by the live twin.</summary>
    public static PlatformDefinition Cmdemo1()
    {
        const string nonprod = "rg-cmdemo1-nonprod";
        const string prod = "rg-cmdemo1-prod";
        return new PlatformDefinition
        {
            PlatformName = "Clear Measure Demo Platform",
            SystemSlug = "cmdemo1",
            SystemName = "Clear Measure work orders demo 1",
            GitHub = new GitHubPlatform
            {
                Org = "clearmeasure-aisf-sample-apps",
                SystemRepository = "cmdemo1-system",
                TwinRepository = "cmdemo1-digitaltwin",
                WorkItemsProjectNumber = 679,
                WorkItemsProjectNodeId = "PVT_kwDOD_ESUc4BliGL",
                DefaultWorkItemRepository = "cmdemo1-workorders",
                TwinHookUrlMarker = "cmdemo1-twin",
                ObservedRepositories =
                [
                    "cmdemo1-system", "cmdemo1-workorders", "cmdemo1-dashboard", "cmdemo1-aisf", "cmdemo1-fworker", "cmdemo1-app2-ui",
                    "cmdemo1-scorecard", "cmdemo1-scorecard-site", "cmdemo1-video", "cmdemo1-digitaltwin"
                ],
                IncrementalRepositories = ["cmdemo1-workorders", "cmdemo1-dashboard", "cmdemo1-system", "cmdemo1-scorecard-site", "cmdemo1-digitaltwin"],
                RetiredRepositories = ["cmdemo1-aisf", "cmdemo1-fworker", "cmdemo1-app2-ui", "cmdemo1-scorecard"],
                StatusFeeds = new GitOpsStatusFeeds()
            },
            Octopus = new OctopusPlatform
            {
                Url = "https://clearmeasure.octopus.app",
                SpaceId = "Spaces-355",
                SpaceSlug = "cmdemo1-demo",
                AutomationUsers = ["cmdemo1-github"]
            },
            Azure = new AzurePlatform
            {
                SubscriptionId = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69",
                TenantId = "40645332-2e20-4bca-882c-b2706f93ce44",
                NonprodResourceGroup = nonprod,
                ProdResourceGroup = prod,
                AcrName = "acrcmdemo1chu4gq",
                AcrLoginServer = "acrcmdemo1chu4gq.azurecr.io"
            },
            DefaultPipelineId = "system-gitops",
            Pipelines =
            [
                new("app-ui", "Work Orders UI", "BusinessApp", "cmdemo1-workorders", "ui", "cmdemo1-ui", false, false,
                    "Primary .NET work-orders application deployed as Azure Container Apps with Azure SQL and Playwright acceptance tests."),
                new("app-dashboard", "Health Dashboard", "BusinessApp", "cmdemo1-dashboard", "dashboard", "cmdemo1-dashboard", false, false,
                    "Blazor WebAssembly system health dashboard deployed to Azure Static Web Apps."),
                new("system-gitops", "System GitOps & Infra", "GitOpsInfra", "cmdemo1-system", "system", "cmdemo1-system", false, false,
                    "Desired state declarations (system.json, Bicep stacks, Octopus Terraform, status feeds, and operational runbooks)."),
                new("measurement-scorecard", "Scorecard & Measurement", "Measurement", "cmdemo1-scorecard-site", "scorecard", "cmdemo1-scorecard-space", false, false,
                    "Nightly Clear Measure Intelligence Scorecard publication to GitHub Pages and prod blob site (runtime scorecard app retired)."),
                new("factory-agentic", "Software Factory & Agents", "SoftwareFactory", "cmdemo1-fworker", "fworker", "cmdemo1-fworker", true, false,
                    "Autonomous bot/agent delivery pipeline across Project 679 and PRs (legacy aisf/fworker/app2-ui runtimes retired)."),
                new("twin-observer", "Digital Twin Observer", "PlatformObserver", "cmdemo1-digitaltwin", null, null, false, true,
                    "Platform-level digital twin sitting one level above cmdemo1 to observe and reconcile without becoming a cmdemo1 deployable.")
            ],
            Stages =
            [
                new("tdd", "nonprod", "cae-cmdemo1-tdd", nonprod, 1, AutoDeploy: true, HasAcceptanceTests: true, RequiresRestorePoint: false),
                new("uat", "nonprod", "cae-cmdemo1-tdd", nonprod, 2, AutoDeploy: false, HasAcceptanceTests: false, RequiresRestorePoint: false),
                new("prod", "prod", "cae-cmdemo1-prod-ekdi", prod, 3, AutoDeploy: false, HasAcceptanceTests: false, RequiresRestorePoint: true),
                new("pool", "nonprod", "cae-cmdemo1-pool", nonprod, 4, AutoDeploy: false, HasAcceptanceTests: true, RequiresRestorePoint: false, IsDeclaredOnly: true)
            ],
            HostingBoundaries =
            [
                new("cae-cmdemo1-tdd", "ContainerAppsEnvironment", nonprod, "southcentralus", ["tdd", "uat"]),
                new("cae-cmdemo1-prod-ekdi", "ContainerAppsEnvironment", prod, "southcentralus", ["prod"]),
                new("swa-centralus", "StaticWebAppsEdge", $"{nonprod},{prod}", "centralus", ["tdd", "uat", "prod"]),
                new("cae-cmdemo1-twin", "ContainerAppsEnvironment (Platform Twin)", nonprod, "southcentralus", ["platform-twin"])
            ],
            RepositoryPipelines = new(StringComparer.OrdinalIgnoreCase)
            {
                ["cmdemo1-workorders"] = "app-ui",
                ["cmdemo1-dashboard"] = "app-dashboard",
                ["cmdemo1-system"] = "system-gitops",
                ["cmdemo1-scorecard"] = "measurement-scorecard",
                ["cmdemo1-scorecard-site"] = "measurement-scorecard",
                ["cmdemo1-fworker"] = "factory-agentic",
                ["cmdemo1-aisf"] = "factory-agentic",
                ["cmdemo1-app2-ui"] = "factory-agentic",
                ["cmdemo1-video"] = "factory-agentic",
                ["cmdemo1-digitaltwin"] = "twin-observer"
            },
            RepositoryDeployables = new(StringComparer.OrdinalIgnoreCase)
            {
                ["cmdemo1-workorders"] = "ui",
                ["cmdemo1-dashboard"] = "dashboard",
                ["cmdemo1-system"] = "system",
                ["cmdemo1-scorecard"] = "scorecard",
                ["cmdemo1-scorecard-site"] = "scorecard",
                ["cmdemo1-aisf"] = "aisf",
                ["cmdemo1-fworker"] = "fworker"
            },
            OctopusProjectPipelines = new(StringComparer.OrdinalIgnoreCase)
            {
                ["cmdemo1-ui"] = "app-ui",
                ["cmdemo1-dashboard"] = "app-dashboard",
                ["cmdemo1-system"] = "system-gitops",
                ["cmdemo1-scorecard"] = "measurement-scorecard",
                ["cmdemo1-scorecard-space"] = "measurement-scorecard",
                ["cmdemo1-aisf"] = "factory-agentic",
                ["cmdemo1-fworker"] = "factory-agentic",
                ["cmdemo1-fworker-runtime"] = "factory-agentic",
                ["cmdemo1-digitaltwin"] = "twin-observer"
            },
            OctopusProjectDeployables = new(StringComparer.OrdinalIgnoreCase)
            {
                ["cmdemo1-ui"] = "ui",
                ["cmdemo1-dashboard"] = "dashboard",
                ["cmdemo1-system"] = "system",
                ["cmdemo1-scorecard"] = "scorecard",
                ["cmdemo1-scorecard-space"] = "scorecard",
                ["cmdemo1-aisf"] = "aisf",
                ["cmdemo1-fworker"] = "fworker",
                ["cmdemo1-fworker-runtime"] = "fworker"
            },
            AzureStageRules =
            [
                new("twin", ["twin"], []),
                new("tdd", ["-tdd", "cmdemo1tdd"], []),
                new("uat", ["-uat", "cmdemo1uat"], []),
                new("prod", ["-prod", "cmdemo1prod"], [prod]),
                new("pool", ["-pool", "cmdemo1pool"], [])
            ],
            DefaultAzureStage = "shared"
        };
    }
}

public sealed class GitHubPlatform
{
    public string Org { get; init; } = "";

    /// <summary>The system's GitOps repository: desired state and the status feeds below.</summary>
    public string SystemRepository { get; init; } = "";

    public string TwinRepository { get; init; } = "";

    /// <summary>Every repository the twin observes (full backfill, hook registration, reconciliation, issue sweep).</summary>
    public List<string> ObservedRepositories { get; init; } = [];

    /// <summary>Repositories polled on the incremental schedule; empty means all observed repositories.</summary>
    public List<string> IncrementalRepositories { get; init; } = [];

    /// <summary>Repositories shown as retired even when not archived on GitHub.</summary>
    public List<string> RetiredRepositories { get; init; } = [];

    /// <summary>Organization project (v2) that is the work board; 0 to skip.</summary>
    public int WorkItemsProjectNumber { get; init; }

    public string WorkItemsProjectNodeId { get; init; } = "";

    /// <summary>Repository assumed for work items that do not name one.</summary>
    public string DefaultWorkItemRepository { get; init; } = "";

    /// <summary>Text in a webhook URL that marks it as this twin's (its Container App host name).</summary>
    public string TwinHookUrlMarker { get; init; } = "";

    public GitOpsStatusFeeds StatusFeeds { get; init; } = new();

    [JsonIgnore]
    public IReadOnlyList<string> PolledRepositories => IncrementalRepositories.Count > 0 ? IncrementalRepositories : ObservedRepositories;
}

/// <summary>Files the system repository publishes; an empty path skips that feed (the twin then relies on the APIs alone).</summary>
public sealed class GitOpsStatusFeeds
{
    public string DesiredStateBranch { get; init; } = "main";
    public string SystemJson { get; init; } = "system.json";

    /// <summary>Per-stage desired versions; "{stage}" is replaced by the stage name.</summary>
    public string StageVersions { get; init; } = "environments/{stage}/versions.json";

    public string StatusBranch { get; init; } = "status";
    public string Cost { get; init; } = "cost.json";
    public string Containers { get; init; } = "containers.json";
    public string WorkItems { get; init; } = "workitems.json";
}

public sealed class OctopusPlatform
{
    public string Url { get; init; } = "";
    public string SpaceId { get; init; } = "";
    public string SpaceSlug { get; init; } = "";

    /// <summary>Octopus users that deploy on behalf of automation; their approvals are not counted as a person signing off.</summary>
    public List<string> AutomationUsers { get; init; } = [];
}

public sealed class AzurePlatform
{
    public string SubscriptionId { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string NonprodResourceGroup { get; init; } = "";
    public string ProdResourceGroup { get; init; } = "";

    /// <summary>Further resource groups to observe besides nonprod and prod.</summary>
    public List<string> AdditionalResourceGroups { get; init; } = [];

    public string AcrName { get; init; } = "";
    public string AcrLoginServer { get; init; } = "";

    [JsonIgnore]
    public IReadOnlyList<string> ResourceGroups => new[] { NonprodResourceGroup, ProdResourceGroup }.Concat(AdditionalResourceGroups)
        .Where(rg => !string.IsNullOrWhiteSpace(rg)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record StageInferenceRule(string Stage, string[] NameContains, string[] ResourceGroups);
