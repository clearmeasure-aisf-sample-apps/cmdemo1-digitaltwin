namespace DigitalTwin.Infrastructure.Options;

public sealed class TwinOptions
{
    public string GitHubToken { get; set; } = string.Empty;
    public string OctopusUrl { get; set; } = "https://clearmeasure.octopus.app";
    public string OctopusApiKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string TwinApiKey { get; set; } = string.Empty;
    public string AzureClientId { get; set; } = string.Empty;
    public string AzureClientSecret { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public string AzureSubscriptionId { get; set; } = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69";
    public string TwinPublicUrl { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = "Data Source=twin.db";
    public bool UseSqlServer { get; set; }
    public bool EnableBackgroundWorkers { get; set; } = true;
    public int GitHubPollSeconds { get; set; } = 300;
    public int OctopusPollSeconds { get; set; } = 45;
    public int AzurePollSeconds { get; set; } = 60;
    public int ProbeIntervalSeconds { get; set; } = 45;
}
