namespace DigitalTwin.GitHub;

public sealed class GitHubOptions
{
    public string Token { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}
