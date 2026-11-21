using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalTwin.Tests.Infrastructure;
using Wolverine.Tracking;
using Xunit;

namespace DigitalTwin.Tests;

[Collection(SqlServerTests.Name)]
public sealed class ApiIntegrationTests(SqlServerFixture sql) : IAsyncLifetime, IDisposable
{
    private const string TestApiKey = TwinAppFactory.ApiKey;
    private const string TestWebhookSecret = TwinAppFactory.WebhookSecret;
    private TwinAppFactory _factory = null!;

    public async Task InitializeAsync() => _factory = new TwinAppFactory(await sql.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory?.Dispose();

    [Fact]
    public async Task PublicEndpointsSucceedAndProtectedEndpointsEnforceTwinKey()
    {
        using var client = _factory.CreateClient();

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        var rootHtml = await root.Content.ReadAsStringAsync();
        Assert.Contains("DevOps Digital Twin", rootHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("fleet", rootHtml, StringComparison.OrdinalIgnoreCase);

        var health = await client.GetAsync("/_healthcheck");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var llms = await client.GetAsync("/llms.txt");
        Assert.Equal(HttpStatusCode.OK, llms.StatusCode);
        var llmsBody = await llms.Content.ReadAsStringAsync();
        Assert.Contains("/mcp", llmsBody, StringComparison.Ordinal);

        var summaryNoKey = await client.GetAsync("/api/twin/summary");
        Assert.Equal(HttpStatusCode.OK, summaryNoKey.StatusCode);
        var summaryBody = await summaryNoKey.Content.ReadAsStringAsync();
        Assert.Contains("\"intakeQueue\"", summaryBody, StringComparison.Ordinal);
        Assert.Contains("\"deadLetterCount\"", summaryBody, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/intake/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/deadletters")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/admin/rebuild-from-raw", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/admin/deadletters/replay", null)).StatusCode);

        var graphNoKey = await client.GetAsync("/api/twin/graph");
        Assert.Equal(HttpStatusCode.OK, graphNoKey.StatusCode);

        // Protected endpoints reject missing key and wrong key
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/drift")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/metrics")).StatusCode);

        using var badSummaryReq = new HttpRequestMessage(HttpMethod.Get, "/api/twin/summary");
        badSummaryReq.Headers.Add("X-Twin-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(badSummaryReq)).StatusCode);

        // With valid X-Twin-Key they return 200
        using var authedReq = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        authedReq.Headers.Add("X-Twin-Key", TestApiKey);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authedReq)).StatusCode);
    }

    [Fact]
    public async Task WebhooksValidateSecretsAndEventGridHandshake()
    {
        using var client = _factory.CreateClient();

        // GitHub unsigned -> 401
        using var badGh = new StringContent("{}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/hooks/github", badGh)).StatusCode);

        // GitHub valid HMAC -> 200
        var ghPayload = """{"zen":"Keep it awesome","repository":{"name":"cmdemo1-digitaltwin","full_name":"clearmeasure-aisf-sample-apps/cmdemo1-digitaltwin"},"sender":{"login":"tester"}}""";
        var ghBytes = Encoding.UTF8.GetBytes(ghPayload);
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestWebhookSecret), ghBytes));
        using var ghReq = new HttpRequestMessage(HttpMethod.Post, "/hooks/github")
        {
            Content = new ByteArrayContent(ghBytes)
        };
        ghReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ghReq.Headers.Add("X-GitHub-Event", "ping");
        ghReq.Headers.Add("X-GitHub-Delivery", "integration-ping-1");
        ghReq.Headers.Add("X-Hub-Signature-256", $"sha256={sig}");
        HttpResponseMessage ghResp = null!;
        await _factory.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync((Func<Wolverine.IMessageContext, Task>)(async _ => ghResp = await client.SendAsync(ghReq)));
        Assert.Equal(HttpStatusCode.OK, ghResp.StatusCode);

        // Octopus valid secret -> 200
        var octoPayload = """{"Timestamp":"2026-11-21T05:00:00Z","EventType":"SubscriptionPayload","Payload":{"ServerUri":"https://clearmeasure.octopus.app","Event":{"Id":"Events-999","Category":"DeploymentSucceeded","Message":"Deploy succeeded","Occurred":"2026-11-21T05:00:00Z","Username":"system","RelatedDocumentIds":[]}}}""";
        using var octoReq = new HttpRequestMessage(HttpMethod.Post, "/hooks/octopus")
        {
            Content = new StringContent(octoPayload, Encoding.UTF8, "application/json")
        };
        octoReq.Headers.Add("X-Webhook-Secret", TestWebhookSecret);
        HttpResponseMessage octoResp = null!;
        await _factory.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync((Func<Wolverine.IMessageContext, Task>)(async _ => octoResp = await client.SendAsync(octoReq)));
        Assert.Equal(HttpStatusCode.OK, octoResp.StatusCode);

        // Azure Event Grid validation handshake -> 200 with validationResponse
        var azVal = """[{"id":"v1","eventType":"Microsoft.EventGrid.SubscriptionValidationEvent","data":{"validationCode":"abc-123"}}]""";
        var azResp = await client.PostAsync("/hooks/azure", new StringContent(azVal, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, azResp.StatusCode);
        var azBody = await azResp.Content.ReadAsStringAsync();
        Assert.Contains("abc-123", azBody, StringComparison.Ordinal);

        // Verify persisted events appear via /api/events
        using var evReq = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        evReq.Headers.Add("X-Twin-Key", TestApiKey);
        var evResp = await client.SendAsync(evReq);
        var evBody = await evResp.Content.ReadAsStringAsync();
        Assert.Contains("github:integration-ping-1", evBody, StringComparison.Ordinal);
        Assert.Contains("octopus:Events-999", evBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpEndpointRequiresKeyAndListsTools()
    {
        using var client = _factory.CreateClient();

        var initPayload = """
            {
              "jsonrpc": "2.0",
              "id": 1,
              "method": "initialize",
              "params": {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": { "name": "test-client", "version": "1.0" }
              }
            }
            """;

        using var noKeyReq = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(initPayload, Encoding.UTF8, "application/json")
        };
        noKeyReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        noKeyReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(noKeyReq)).StatusCode);

        using var authedInit = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(initPayload, Encoding.UTF8, "application/json")
        };
        authedInit.Headers.Add("X-Twin-Key", TestApiKey);
        authedInit.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        authedInit.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var initResp = await client.SendAsync(authedInit);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);

        var toolsCallPayload = """
            {
              "jsonrpc": "2.0",
              "id": 2,
              "method": "tools/list",
              "params": {}
            }
            """;
        using var listReq = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(toolsCallPayload, Encoding.UTF8, "application/json")
        };
        listReq.Headers.Add("X-Twin-Key", TestApiKey);
        listReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        listReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var listResp = await client.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listBody = await listResp.Content.ReadAsStringAsync();
        Assert.Contains("get_summary", listBody, StringComparison.Ordinal);
        Assert.Contains("get_intake_queue", listBody, StringComparison.Ordinal);
        Assert.Contains("explain_item", listBody, StringComparison.Ordinal);

        var callPayload = """
            {
              "jsonrpc": "2.0",
              "id": 3,
              "method": "tools/call",
              "params": {
                "name": "get_summary",
                "arguments": {}
              }
            }
            """;
        using var callReq = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(callPayload, Encoding.UTF8, "application/json")
        };
        callReq.Headers.Add("X-Twin-Key", TestApiKey);
        callReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        callReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var callResp = await client.SendAsync(callReq);
        Assert.Equal(HttpStatusCode.OK, callResp.StatusCode);
        var callBody = await callResp.Content.ReadAsStringAsync();
        Assert.Contains("cmdemo1", callBody, StringComparison.Ordinal);
        Assert.DoesNotContain("fleet", callBody, StringComparison.OrdinalIgnoreCase);
    }
}
