using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Notifications;

public enum AlertFormat
{
    Slack,
    Teams,
    Generic
}

/// <summary>Bound from <c>Twin:Alerts</c>. The URL is a secret (Container App secret), empty by default: alerts off.</summary>
public sealed class AlertOptions
{
    public string WebhookUrl { get; set; } = string.Empty;
    public AlertFormat Format { get; set; } = AlertFormat.Slack;
}

/// <summary>
/// Posts alerts to an incoming webhook: Slack (<c>{"text": …}</c>), Teams (MessageCard) or the raw alert as JSON.
/// Does nothing while no URL is configured.
/// </summary>
public sealed class WebhookAlertSink(IHttpClientFactory httpClientFactory, IOptions<AlertOptions> options) : IAlertSink
{
    public const string HttpClientName = "alerts";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public bool IsEnabled => Uri.TryCreate(options.Value.WebhookUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public string Format => options.Value.Format.ToString();

    public async Task SendAsync(TwinAlert alert, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return;
        }
        object payload = options.Value.Format switch
        {
            AlertFormat.Slack => new { text = $"*{alert.Title}*\n{alert.Text}{(alert.Url is null ? "" : $"\n<{alert.Url}|Open>")}" },
            AlertFormat.Teams => new Dictionary<string, object?>
            {
                ["@type"] = "MessageCard",
                ["@context"] = "https://schema.org/extensions",
                ["summary"] = alert.Title,
                ["themeColor"] = alert.Severity == "critical" ? "D13438" : alert.Severity == "warning" ? "FFB900" : "0078D7",
                ["title"] = alert.Title,
                ["text"] = alert.Text + (alert.Url is null ? "" : $"<br/><a href=\"{alert.Url}\">Open</a>")
            },
            AlertFormat.Generic => alert,
            _ => throw new ArgumentOutOfRangeException(nameof(alert), options.Value.Format, "Unknown alert format")
        };
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(options.Value.WebhookUrl, payload, Json, ct);
        response.EnsureSuccessStatusCode();
    }
}

public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddTwinNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(WebhookAlertSink.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.Configure<AlertOptions>(configuration.GetSection("Twin:Alerts"));
        services.AddSingleton<IAlertSink, WebhookAlertSink>();
        return services;
    }
}
