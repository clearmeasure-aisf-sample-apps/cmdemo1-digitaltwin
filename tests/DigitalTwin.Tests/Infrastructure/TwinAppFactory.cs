using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalTwin.Tests.Infrastructure;

/// <summary>Twin host against a real SQL Server database with no Azure, GitHub or Octopus credentials.</summary>
public sealed class TwinAppFactory(
    string connectionString,
    IDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-twin-api-key-123";
    public const string WebhookSecret = "test-webhook-secret-456";

    public string ConnectionString { get; } = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting (unlike ConfigureAppConfiguration) is visible while Program registers services.
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:TwinDb"] = ConnectionString,
            ["TWIN_API_KEY"] = ApiKey,
            ["WEBHOOK_SECRET"] = WebhookSecret,
            ["Twin:EnableBackgroundWorkers"] = "false",
            ["Twin:Messaging:Transport"] = "LocalDurable",
            ["Twin:Messaging:DurabilityMode"] = "Solo",
            ["Twin:Messaging:RetryDelays"] = "50,100,150"
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }
        foreach (var (key, value) in values)
        {
            builder.UseSetting(key, value);
        }
        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }

    public HttpClient CreateAuthedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Twin-Key", ApiKey);
        return client;
    }

    public static HttpRequestMessage GitHubWebhook(string eventName, string deliveryId, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), bytes));
        var req = new HttpRequestMessage(HttpMethod.Post, "/hooks/github") { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-GitHub-Event", eventName);
        req.Headers.Add("X-GitHub-Delivery", deliveryId);
        req.Headers.Add("X-Hub-Signature-256", $"sha256={sig}");
        return req;
    }

    public static HttpRequestMessage OctopusWebhook(string eventId, string message = "Deploy succeeded")
    {
        var payload = $$$$"""{"Timestamp":"2026-11-21T05:00:00Z","EventType":"SubscriptionPayload","Payload":{"ServerUri":"https://clearmeasure.octopus.app","Event":{"Id":"{{{{eventId}}}}","Category":"DeploymentSucceeded","Message":"{{{{message}}}}","Occurred":"2026-11-21T05:00:00Z","Username":"system","RelatedDocumentIds":[]}}}""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/hooks/octopus") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Webhook-Secret", WebhookSecret);
        return req;
    }

    public static HttpRequestMessage AzureWebhook(string eventId)
    {
        var payload = $$"""[{"id":"{{eventId}}","topic":"/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod","subject":"/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/ca-cmdemo1-tdd-ui","eventType":"Microsoft.Resources.ResourceWriteSuccess","eventTime":"2026-11-21T05:00:00Z","data":{"operationName":"Microsoft.App/containerApps/write","status":"Succeeded","resourceUri":"/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/ca-cmdemo1-tdd-ui"},"dataVersion":"","metadataVersion":"1"}]""";
        var req = new HttpRequestMessage(HttpMethod.Post, "/hooks/azure") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Webhook-Secret", WebhookSecret);
        return req;
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s waiting until {because}.");
    }
}
