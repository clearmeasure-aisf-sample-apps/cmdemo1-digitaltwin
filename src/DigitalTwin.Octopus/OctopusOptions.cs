namespace DigitalTwin.Octopus;

public sealed class OctopusOptions
{
    public string Url { get; set; } = "https://clearmeasure.octopus.app";
    public string ApiKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}
