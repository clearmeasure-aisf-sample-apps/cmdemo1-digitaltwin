using System.Globalization;
using System.Text;

namespace DigitalTwin.Core.Normalization;

/// <summary>
/// Deterministic twin event types and domain names for source events without a hand-written mapping:
/// GitHub <c>twin.github.{event}.{action}</c> and Octopus <c>twin.octopus.{area}.{verb}</c>.
/// </summary>
public static class SourceEventTypes
{
    public const string PlatformAdministration = "Platform Administration";

    // Longest prefix wins. Area names are the twin's, not Octopus's.
    private static readonly (string Prefix, string Area)[] OctopusAreas =
    [
        ("EphemeralEnvironmentDeployment", "ephemeralDeployment"), ("EphemeralEnvironment", "ephemeral"),
        ("KubernetesResource", "kubernetes"), ("ArgoCDApplication", "argoCD"), ("PullRequestCompletion", "pullRequest"),
        ("WebhookTriggeredRunbookRun", "webhookTrigger"), ("WebhookTrigger", "webhookTrigger"),
        ("CompliancePolicyEvaluated", "compliance"), ("BuildInformation", "buildInformation"), ("AutoDeployTrigger", "autoDeploy"),
        ("UsageDeclaration", "usageDeclaration"), ("LicenseUsage", "license"), ("LicenseKey", "license"),
        ("SigningKeyRotation", "signingKey"), ("RunbookSnapshot", "runbookSnapshot"), ("RunbookRun", "runbookRun"),
        ("ProjectExport", "projectExport"), ("ProjectImport", "projectImport"), ("UserPassword", "user"),
        ("ManualIntervention", "interruption"), ("GuidedFailure", "interruption"),
        ("Certificate", "certificate"), ("DailyUsage", "usage"), ("Deployment", "deployment"), ("IpAddress", "ipAddress"),
        ("McpOAuth", "mcpOAuth"), ("OAuth", "oauth"), ("ApiKey", "apiKey"), ("Machine", "machine"), ("Package", "package"),
        ("Release", "release"), ("RateLimit", "rateLimit"), ("Artifact", "artifact"), ("Attachments", "attachment"),
        ("Login", "login"), ("Task", "task")
    ];

    private static readonly HashSet<string> AdministrationAreas = new(StringComparer.Ordinal)
    {
        "apiKey", "certificate", "license", "login", "mcpOAuth", "oauth", "signingKey", "usageDeclaration", "usage", "user", "ipAddress"
    };

    private static readonly HashSet<string> RuntimeAreas = new(StringComparer.Ordinal)
    {
        "machine", "kubernetes", "ephemeral", "ephemeralDeployment", "argoCD"
    };

    private static readonly HashSet<string> BuildAreas = new(StringComparer.Ordinal) { "package", "buildInformation", "artifact" };

    public static string OctopusType(string category)
    {
        var (area, verb) = OctopusParts(category);
        return $"twin.octopus.{area}.{verb}";
    }

    public static string OctopusContext(string category)
    {
        var area = OctopusParts(category).Area;
        return AdministrationAreas.Contains(area) ? PlatformAdministration
            : RuntimeAreas.Contains(area) ? SourceEventCatalog.RuntimeAndPlatform
            : BuildAreas.Contains(area) ? SourceEventCatalog.BuildAndArtifact
            : SourceEventCatalog.ReleaseAndDelivery;
    }

    private static (string Area, string Verb) OctopusParts(string category)
    {
        foreach (var (prefix, area) in OctopusAreas)
        {
            if (category.StartsWith(prefix, StringComparison.Ordinal) && category.Length > prefix.Length)
            {
                return (area, Camel(category[prefix.Length..]));
            }
        }
        // Single-word document categories: Comment, Downloaded, Exported, Imported, Created, Modified, Deleted.
        return ("document", Camel(category));
    }

    public static string GitHubType(string eventName, string? action)
        => $"twin.github.{eventName}{(string.IsNullOrEmpty(action) ? "" : $".{action}")}";

    public static string GitHubDomainEvent(string eventName, string? action) => Pascal(eventName) + (action is null ? "" : Pascal(action));

    public static string GitHubContext(string eventName) => eventName switch
    {
        _ when eventName.Contains("alert", StringComparison.Ordinal) || eventName.Contains("advisory", StringComparison.Ordinal)
               || eventName.StartsWith("secret_scanning", StringComparison.Ordinal) || eventName == "security_and_analysis"
            => SourceEventCatalog.ReliabilityAndDrift,
        "label" or "milestone" or "sub_issues" or "issue_dependencies" or "discussion" or "discussion_comment"
            or "project" or "project_card" or "project_column" or "projects_v2" or "projects_v2_item" or "projects_v2_status_update"
            => SourceEventCatalog.Work,
        "deployment_review" or "deployment_protection_rule" => SourceEventCatalog.ReleaseAndDelivery,
        "workflow_dispatch" or "page_build" or "merge_group" => SourceEventCatalog.BuildAndArtifact,
        "commit_comment" or "pull_request_review_comment" or "pull_request_review_thread" or "gollum" or "status" or "fork"
            => SourceEventCatalog.SourceAndChange,
        _ => PlatformAdministration
    };

    private static string Camel(string pascal) => pascal.Length == 0 ? pascal : char.ToLower(pascal[0], CultureInfo.InvariantCulture) + pascal[1..];

    private static string Pascal(string snake)
    {
        var sb = new StringBuilder();
        foreach (var part in snake.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ToUpper(part[0], CultureInfo.InvariantCulture)).Append(part[1..]);
        }
        return sb.ToString();
    }
}
