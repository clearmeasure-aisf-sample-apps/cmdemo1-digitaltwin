using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalTwin.Application.Models;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.Work;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

/// <summary>Point-in-time queries over fixed event fixtures observed at known instants.</summary>
[Collection(SqlServerTests.Name)]
public sealed class TimeTravelTests(SqlServerFixture sql)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset AtTdd = F.T0.AddMinutes(48);
    private static readonly DateTimeOffset AtUat = F.T0.AddMinutes(64);
    private static readonly DateTimeOffset AtProd = F.T0.AddMinutes(96);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AsOfQueriesReplayStateAtEachInstantAndNowMatchesLiveReadModels()
    {
        var clock = new ManualClock(AtTdd);
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), configureServices: s => s.AddSingleton<TimeProvider>(clock));
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();

        // tdd deployed; an old container app still exists; prod trails (drift); the issue is in progress.
        await Track(app, async () =>
        {
            await state.PublishAsync(StateKinds.Repository, [F.Repository()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.WorkItem, [F.WorkItem()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.SourceChange, [F.PullRequest()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.PipelineRun, [F.Build()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Artifact, [F.Artifact()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47)], EventSourceKind.Octopus, default);
            await state.PublishSetAsync(StateKinds.AzureResource, "azure", [F.Resource("ca-cmdemo1-tdd-ui"), F.Resource("ca-cmdemo1-old")], EventSourceKind.Azure, default);
            await state.PublishSetAsync(StateKinds.Workload, "azure", [F.Workload("tdd", F.Version)], EventSourceKind.Azure, default);
            await state.PublishSetAsync(StateKinds.Drift, "cmdemo1", [F.Drift()], EventSourceKind.Twin, default);
            await state.PublishSetAsync(StateKinds.WebhookStatus, "github", [F.Hook("cmdemo1-workorders")], EventSourceKind.GitHub, default);
        });

        clock.Now = AtUat;
        await Track(app, async () =>
        {
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("uat", "succeeded", 60, 63)], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.WorkItem, [WithColumn(F.WorkItem(), "UAT", "open")], EventSourceKind.GitHub, default);
            await state.PublishSetAsync(StateKinds.Workload, "azure", [F.Workload("tdd", F.Version), F.Workload("uat", F.Version)], EventSourceKind.Azure, default);
        });

        // prod deployed; the old app is deleted; drift resolves; the issue is done.
        clock.Now = AtProd;
        await Track(app, async () =>
        {
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("prod", "succeeded", 90, 95)], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.WorkItem, [WithColumn(F.WorkItem(), "Done", "closed")], EventSourceKind.GitHub, default);
            await state.PublishSetAsync(StateKinds.AzureResource, "azure", [F.Resource("ca-cmdemo1-tdd-ui")], EventSourceKind.Azure, default);
            await state.PublishSetAsync(StateKinds.Drift, "cmdemo1", Array.Empty<DriftFinding>(), EventSourceKind.Twin, default);
        });

        clock.Now = F.T0.AddDays(1);

        var beforeHistory = await StateAt(client, F.T0);
        Assert.True(beforeHistory.BeforeHistory);
        Assert.Empty(beforeHistory.AzureResources);
        Assert.All(beforeHistory.Environments, e => Assert.Null(e.DeployedVersions.GetValueOrDefault("ui")));

        var tdd = await StateAt(client, AtTdd.AddMinutes(1));
        Assert.False(tdd.BeforeHistory);
        Assert.Equal(F.Version, Stage(tdd, "tdd").DeployedVersions["ui"]);
        Assert.Null(Stage(tdd, "uat").DeployedVersions["ui"]);
        Assert.Null(Stage(tdd, "prod").DeployedVersions["ui"]);
        Assert.Equal(["ca-cmdemo1-old", "ca-cmdemo1-tdd-ui"], tdd.AzureResources.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, tdd.BoardColumns["In Progress"]);
        Assert.Single(tdd.ActiveDrift);

        var uat = await StateAt(client, AtUat.AddMinutes(1));
        Assert.Equal(F.Version, Stage(uat, "uat").DeployedVersions["ui"]);
        Assert.Null(Stage(uat, "prod").DeployedVersions["ui"]);
        Assert.Equal(1, uat.BoardColumns["UAT"]);
        Assert.Equal(["tdd:ui", "uat:ui"], uat.Environments.SelectMany(e => e.Workloads.Select(w => $"{e.Stage}:{w.Deployable}")).Order(StringComparer.Ordinal));

        var prod = await StateAt(client, AtProd.AddMinutes(1));
        Assert.Equal(F.Version, Stage(prod, "prod").DeployedVersions["ui"]);
        Assert.Equal("succeeded", Stage(prod, "prod").LastDeployment?.State);
        Assert.Equal(["ca-cmdemo1-tdd-ui"], prod.AzureResources.Select(r => r.Name));
        Assert.Equal(1, prod.BoardColumns["Done"]);
        Assert.Equal(0, prod.OpenWorkItems);
        Assert.Empty(prod.ActiveDrift);

        // Every read endpoint honours asOf.
        var deploymentsAtUat = await client.GetFromJsonAsync<List<JsonElement>>($"/api/deployments?asOf={Iso(AtUat.AddMinutes(1))}", Json);
        Assert.Equal(["tdd", "uat"], deploymentsAtUat!.Select(d => d.GetProperty("stageName").GetString()).Order(StringComparer.Ordinal));
        var driftAtProd = await client.GetFromJsonAsync<List<JsonElement>>($"/api/drift?includeResolved=true&asOf={Iso(AtProd.AddMinutes(1))}", Json);
        var resolved = Assert.Single(driftAtProd!);
        Assert.Equal(AtProd, resolved.GetProperty("resolvedAt").GetDateTimeOffset());
        var summaryAtTdd = await client.GetFromJsonAsync<JsonElement>($"/api/twin/summary?asOf={Iso(AtTdd.AddMinutes(1))}", Json);
        Assert.Equal(AtTdd.AddMinutes(1), summaryAtTdd.GetProperty("asOf").GetDateTimeOffset());
        var eventsAtTdd = await client.GetFromJsonAsync<List<JsonElement>>($"/api/events?eventType={StateKinds.EventType}&limit=500&asOf={Iso(AtTdd.AddMinutes(1))}", Json);
        Assert.All(eventsAtTdd!, e => Assert.True(e.GetProperty("occurredAt").GetDateTimeOffset() <= AtTdd.AddMinutes(1)));

        // MCP: the same point-in-time answer.
        var mcp = JsonSerializer.Deserialize<PointInTimeStateDto>(await McpCallAsync(client, "get_state_at", new { asOf = AtUat.AddMinutes(1) }), Json)!;
        Assert.Equal(F.Version, Stage(mcp, "uat").DeployedVersions["ui"]);
        Assert.Null(Stage(mcp, "prod").DeployedVersions["ui"]);

        // "As of just now" folds the journal to exactly the live projection.
        var timeTravel = app.Services.GetRequiredService<TwinTimeTravel>();
        var live = app.Services.GetRequiredService<ITwinStore>();
        var pointInTime = await timeTravel.ResolveAsync(clock.Now.AddTicks(-1), default);
        Assert.NotNull(pointInTime.AsOf);
        Assert.Equal(await DumpAsync(live), await DumpAsync(pointInTime.Model));
    }

    private static WorkItemSnapshot WithColumn(WorkItemSnapshot item, string column, string state)
    {
        item.Column = column;
        item.State = state;
        return item;
    }

    private static StageAtDto Stage(PointInTimeStateDto state, string stage) => state.Environments.Single(e => e.Stage == stage);

    private static string Iso(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<PointInTimeStateDto> StateAt(HttpClient client, DateTimeOffset asOf)
    {
        var response = await client.GetAsync($"/api/state-at?asOf={Iso(asOf)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PointInTimeStateDto>(Json))!;
    }

    private static async Task<string> McpCallAsync(HttpClient client, string tool, object arguments)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..];
        using var envelope = JsonDocument.Parse(data);
        return envelope.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static async Task Track(TwinAppFactory app, Func<Task> action)
        => await app.Services.TrackActivity().Timeout(Timeout).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => action()));

    private static async Task<Dictionary<string, string>> DumpAsync(ITwinReadModel model)
    {
        string J<T>(IEnumerable<T> rows) => JsonSerializer.Serialize(rows, IntakeJson.Options);
        return new Dictionary<string, string>
        {
            ["repositories"] = J(await model.GetRepositoriesAsync(default)),
            ["sourceChanges"] = J(await model.GetSourceChangesAsync(default)),
            ["runs"] = J(await model.GetPipelineRunsAsync(int.MaxValue, default)),
            ["artifacts"] = J(await model.GetArtifactsAsync(default)),
            ["workItems"] = J(await model.GetWorkItemsAsync(default)),
            ["releases"] = J(await model.GetReleasesAsync(int.MaxValue, default)),
            ["deliveries"] = J(await model.GetDeliveriesAsync(int.MaxValue, default)),
            ["runbooks"] = J(await model.GetRunbooksAsync(int.MaxValue, default)),
            ["freezes"] = J(await model.GetFreezesAsync(default)),
            ["desired"] = JsonSerializer.Serialize(await model.GetDesiredConfigAsync(default), IntakeJson.Options),
            ["environments"] = J(await model.GetEnvironmentInstancesAsync(default)),
            ["workloads"] = J(await model.GetWorkloadsAsync(default)),
            ["azure"] = J(await model.GetAzureResourcesAsync(default)),
            ["probes"] = J(await model.GetHealthProbesAsync(default)),
            ["drift"] = J(await model.GetDriftFindingsAsync(includeResolved: true, default)),
            ["webhooks"] = J(await model.GetWebhookStatusesAsync(default))
        };
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
