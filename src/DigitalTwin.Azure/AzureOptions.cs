namespace DigitalTwin.Azure;

public sealed class AzureOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69";
    public string WebhookSecret { get; set; } = string.Empty;
}
