namespace DigitalTwin.Api.Auth;

public sealed class TwinHostOptions
{
    public string TwinApiKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string TwinPublicUrl { get; set; } = string.Empty;
}
