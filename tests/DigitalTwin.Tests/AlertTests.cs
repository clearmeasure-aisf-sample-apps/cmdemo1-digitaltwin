using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

public sealed class AlertRuleTests
{
    private static readonly AlertRuleOptions Options = new();
    private static readonly IntakeQueueStatus Healthy = new("SqlServer", true, 0, null, null, 0, 0, 0, null, null, 0, 0, null);

    private static IReadOnlyList<ChangeChain> Chains(params DigitalTwin.Core.ReleaseAndDelivery.DeliveryExecutionSnapshot[] deliveries)
        => ChangeChainBuilder.Build(new ChangeChainSources([F.WorkItem()], [F.PullRequest()], [F.Build()], [F.Artifact()], [F.Release()], deliveries), F.T0.AddHours(3));

    [Fact]
    public void AFailedProdDeployIsCriticalWithinTheLookback()
    {
        var chains = Chains(F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("uat", "succeeded", 60, 63), F.Delivery("prod", "failed", 90, 92));
        var alert = Assert.Single(AlertRules.Evaluate(chains, Healthy, null, F.T0.AddHours(3), Options));
        Assert.Equal(AlertKind.DeployFailed, alert.Kind);
        Assert.Equal("critical", alert.Severity);
        Assert.Equal("deploy-failed:Deployments-prod", alert.Key);
        Assert.Empty(AlertRules.Evaluate(chains, Healthy, null, F.T0.AddDays(2), Options));
    }

    [Fact]
    public void AnIdleUndeliveredChangeIsStalled()
    {
        var chains = Chains(F.Delivery("tdd", "succeeded", 44, 47));
        Assert.Empty(AlertRules.Evaluate(chains, Healthy, null, F.T0.AddHours(3), Options));
        var alert = Assert.Single(AlertRules.Evaluate(chains, Healthy, null, F.T0.AddHours(8), Options));
        Assert.Equal(AlertKind.StalledChange, alert.Kind);
        Assert.Equal("stalled:ui@2.4.2664:Tdd", alert.Key);
        Assert.Empty(AlertRules.Evaluate(chains, Healthy, null, F.T0.AddDays(9), Options));
    }

    [Fact]
    public void AChangeSupersededByANewerVersionAtTheSameStageIsNotStalled()
    {
        var older = Assert.Single(Chains(F.Delivery("tdd", "succeeded", 44, 47)));
        var newer = older with { ChainId = "ui@2.4.2665", Version = "2.4.2665", LastActivityAt = older.LastActivityAt.AddMinutes(30) };
        var alert = Assert.Single(AlertRules.Evaluate([older, newer], Healthy, null, F.T0.AddHours(8), Options));
        Assert.Equal("stalled:ui@2.4.2665:Tdd", alert.Key);
    }

    [Fact]
    public void DeadLettersAndDriftRaiseAlerts()
    {
        var intake = Healthy with { DeadLetterCount = 3 };
        var report = new ReconciliationReport(F.T0, F.T0, 1, 0, [new ReconciliationCheck("GitHub", "open pull requests", "cmdemo1-workorders", 1, 2, ["cmdemo1-workorders#1"], [])]);
        var alerts = AlertRules.Evaluate([], intake, report, F.T0, Options);
        Assert.Contains(alerts, a => a is { Kind: AlertKind.DeadLetters, Key: "dead-letters:3", Severity: "critical" });
        Assert.Contains(alerts, a => a.Kind == AlertKind.ReconciliationDrift && a.Text.Contains("cmdemo1-workorders", StringComparison.Ordinal));
    }
}

[Collection(SqlServerTests.Name)]
public sealed class AlertWebhookTests(SqlServerFixture sql)
{
    [Fact]
    public async Task NewConditionsAreSentOnceToTheConfiguredWebhookAfterTheBaseline()
    {
        var hook = new CapturingHandler();
        var clock = new ManualClock(F.T0.AddHours(2));
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Twin:Alerts:WebhookUrl"] = "https://alerts.example.test/hook",
            ["Twin:Alerts:Format"] = "Teams"
        }, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddHttpClient("alerts").ConfigurePrimaryHttpMessageHandler(() => hook);
        });
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        await Publish(app, () => state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default));
        await Publish(app, () => state.PublishAsync(StateKinds.PipelineRun, [F.Build()], EventSourceKind.GitHub, default));
        await Publish(app, () => state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("prod", "failed", 90, 92)], EventSourceKind.Octopus, default));

        // Baseline: what is already true when the twin starts is not re-sent.
        var baseline = await (await client.PostAsync("/api/admin/alerts/evaluate", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, baseline.GetProperty("active").GetArrayLength());
        Assert.Empty(hook.Bodies);

        // A new failure afterwards is sent once, as a Teams MessageCard.
        await Publish(app, () => state.PublishAsync(StateKinds.Delivery, [F.Delivery("uat", "failed", 60, 63)], EventSourceKind.Octopus, default));
        await client.PostAsync("/api/admin/alerts/evaluate", null);
        await client.PostAsync("/api/admin/alerts/evaluate", null);
        var body = Assert.Single(hook.Bodies);
        using var card = JsonDocument.Parse(body);
        Assert.Equal("MessageCard", card.RootElement.GetProperty("@type").GetString());
        Assert.StartsWith("Deploy to uat failed", card.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAWebhookUrlNothingIsPosted()
    {
        var hook = new CapturingHandler();
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), configureServices: services => services.AddHttpClient("alerts").ConfigurePrimaryHttpMessageHandler(() => hook));
        using var client = app.CreateAuthedClient();
        var sent = await (await client.PostAsync("/api/admin/alerts/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(sent.GetProperty("delivered").GetBoolean());
        Assert.Empty(hook.Bodies);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/alerts")).GetProperty("sinkEnabled").GetBoolean());
    }

    private static async Task Publish(TwinAppFactory app, Func<Task> action)
        => await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => action()));

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
