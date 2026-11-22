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

/// <summary>
/// The platform the twin observes, as the twin's static API. <see cref="Current"/> is chosen once at startup by the composition root
/// (configuration <c>Twin:Platform</c>); it defaults to cmdemo1 (<see cref="PlatformDefinition.Cmdemo1"/>).
/// </summary>
public static class PlatformCatalog
{
    private static PlatformDefinition current = PlatformDefinition.Cmdemo1();

    public static PlatformDefinition Current => current;

    /// <summary>Selects the observed platform. Call before the host starts; the definition is validated first.</summary>
    public static void Use(PlatformDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        current = definition;
    }

    public static string PlatformName => current.PlatformName;
    public static string SystemSlug => current.SystemSlug;
    public static string SystemName => current.SystemName;
    public static string TwinServiceName => current.TwinServiceName;
    public static string GitHubOrg => current.GitHub.Org;
    public static string SystemRepository => current.GitHub.SystemRepository;
    public static string TwinRepository => current.GitHub.TwinRepository;
    public static string SubscriptionId => current.Azure.SubscriptionId;
    public static string TenantId => current.Azure.TenantId;
    public static string NonprodResourceGroup => current.Azure.NonprodResourceGroup;
    public static string ProdResourceGroup => current.Azure.ProdResourceGroup;
    public static IReadOnlyList<string> ResourceGroups => current.Azure.ResourceGroups;
    public static string AcrName => current.Azure.AcrName;
    public static string AcrLoginServer => current.Azure.AcrLoginServer;
    public static string OctopusUrl => current.Octopus.Url;
    public static string OctopusSpaceId => current.Octopus.SpaceId;
    public static string OctopusSpaceSlug => current.Octopus.SpaceSlug;
    public static int WorkItemsProjectNumber => current.GitHub.WorkItemsProjectNumber;
    public static string WorkItemsProjectNodeId => current.GitHub.WorkItemsProjectNodeId;

    public static IReadOnlyList<PipelineDefinition> Pipelines => current.Pipelines;
    public static IReadOnlyList<DeploymentStageDefinition> Stages => current.Stages;
    public static IReadOnlyList<HostingBoundaryDefinition> HostingBoundaries => current.HostingBoundaries;
    public static IReadOnlyList<string> ObservedRepositories => current.GitHub.ObservedRepositories;
    public static IReadOnlyList<string> DeliveryStages => current.DeliveryStageNames;
    public static string FirstStage => current.FirstStage;
    public static string ProductionStage => current.ProductionStage;

    public static string ResolvePipelineIdFromRepository(string? repositoryName) => current.ResolvePipelineIdFromRepository(repositoryName);
    public static string ResolvePipelineIdFromOctopusProject(string? projectSlugOrName) => current.ResolvePipelineIdFromOctopusProject(projectSlugOrName);
    public static string? ResolveDeployableFromRepository(string? repositoryName) => current.ResolveDeployableFromRepository(repositoryName);
    public static string? ResolveDeployableFromOctopusProject(string? projectSlug) => current.ResolveDeployableFromOctopusProject(projectSlug);
    public static string? InferStageFromAzureResourceName(string? resourceName, string? resourceGroup) => current.InferStageFromAzureResourceName(resourceName, resourceGroup);
}
