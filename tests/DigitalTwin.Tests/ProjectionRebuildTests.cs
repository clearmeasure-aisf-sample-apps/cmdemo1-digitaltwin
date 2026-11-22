using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Application.Intake;
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

[Collection(SqlServerTests.Name)]
public sealed class ProjectionRebuildTests(SqlServerFixture sql)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ReadModelsAreProjectedFromStateEventsAndRebuildYieldsIdenticalModels()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        var store = app.Services.GetRequiredService<ITwinStore>();

        // Wave 1: initial observations from every poller kind, plus a webhook.
        await Track(app, async () =>
        {
            await state.PublishAsync(StateKinds.Repository, [F.Repository()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.WorkItem, [F.WorkItem()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.SourceChange, [F.PullRequest()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.PipelineRun, [F.Build()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Artifact, [F.Artifact()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("uat", "succeeded", 60, 63), F.Delivery("prod", "executing", 90, null)], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.HealthProbe, [F.Probe("tdd", F.Version, 40)], EventSourceKind.Probe, default);
            await state.PublishSetAsync(StateKinds.AzureResource, "azure", [F.Resource("ca-cmdemo1-tdd-ui"), F.Resource("ca-cmdemo1-old")], EventSourceKind.Azure, default);
            await state.PublishSetAsync(StateKinds.Workload, "azure", [F.Workload("tdd", F.Version)], EventSourceKind.Azure, default);
            await state.PublishSetAsync(StateKinds.Drift, "cmdemo1", [F.Drift()], EventSourceKind.Twin, default);
            await state.PublishSetAsync(StateKinds.WebhookStatus, "github", [F.Hook("cmdemo1-workorders")], EventSourceKind.GitHub, default);
            await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "projection-1", """{"zen":"x","repository":{"name":"cmdemo1-workorders"}}"""));
        });

        // Wave 2: prod finishes, a resource is deleted, drift resolves; latency-only probe change is not a state change.
        await Track(app, async () =>
        {
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("prod", "succeeded", 90, 95)], EventSourceKind.Octopus, default);
            await state.PublishSetAsync(StateKinds.AzureResource, "azure", [F.Resource("ca-cmdemo1-tdd-ui")], EventSourceKind.Azure, default);
            await state.PublishSetAsync(StateKinds.Drift, "cmdemo1", Array.Empty<DigitalTwin.Core.ReliabilityAndDrift.DriftFinding>(), EventSourceKind.Twin, default);
        });
        Assert.Equal(0, await state.PublishAsync(StateKinds.HealthProbe, [F.Probe("tdd", F.Version, 95)], EventSourceKind.Probe, default));
        Assert.Equal(0, await state.PublishAsync(StateKinds.Repository, [F.Repository()], EventSourceKind.GitHub, default));
        Assert.False(await state.PublishSetAsync(StateKinds.AzureResource, "azure", [F.Resource("ca-cmdemo1-tdd-ui")], EventSourceKind.Azure, default));

        Assert.Equal("succeeded", (await store.GetDeliveriesAsync(10, default)).Single(d => d.StageName == "prod").State);
        Assert.Equal(["ca-cmdemo1-tdd-ui"], (await store.GetAzureResourcesAsync(default)).Select(r => r.Name));
        Assert.Empty(await store.GetDriftFindingsAsync(includeResolved: false, default));
        Assert.Single(await store.GetDriftFindingsAsync(includeResolved: true, default));

        var before = await DumpReadModelsAsync(store);
        var response = await client.PostAsync("/api/admin/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<IntakeRebuildReport>(Json);
        Assert.NotNull(report);
        Assert.True(report.Identical, string.Join("; ", report.Differences));
        Assert.Equal(0, report.RawEventsFailed);

        var after = await DumpReadModelsAsync(store);
        Assert.Equal(before, after);
    }

    private static async Task Track(TwinAppFactory app, Func<Task> action)
        => await app.Services.TrackActivity().Timeout(Timeout).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => action()));

    /// <summary>Full dump of every read model through the public port, independent of the fingerprint implementation.</summary>
    private static async Task<Dictionary<string, string>> DumpReadModelsAsync(ITwinStore store)
    {
        string J<T>(IEnumerable<T> rows, Func<T, string> key) => JsonSerializer.Serialize(rows.OrderBy(key, StringComparer.Ordinal), IntakeJson.Options);
        return new Dictionary<string, string>
        {
            ["repositories"] = J(await store.GetRepositoriesAsync(default), r => r.Name),
            ["sourceChanges"] = J(await store.GetSourceChangesAsync(default), r => r.Id),
            ["runs"] = J(await store.GetPipelineRunsAsync(int.MaxValue, default), r => r.Id),
            ["artifacts"] = J(await store.GetArtifactsAsync(default), r => r.Id),
            ["workItems"] = J(await store.GetWorkItemsAsync(default), r => r.Id),
            ["releases"] = J(await store.GetReleasesAsync(int.MaxValue, default), r => r.ReleaseId),
            ["deliveries"] = J(await store.GetDeliveriesAsync(int.MaxValue, default), r => r.DeploymentId),
            ["environments"] = J(await store.GetEnvironmentInstancesAsync(default), r => r.StageName),
            ["workloads"] = J(await store.GetWorkloadsAsync(default), r => r.Id),
            ["azure"] = J(await store.GetAzureResourcesAsync(default), r => r.ResourceId),
            ["probes"] = J(await store.GetHealthProbesAsync(default), r => r.TargetKey),
            ["drift"] = J(await store.GetDriftFindingsAsync(includeResolved: true, default), r => r.DriftKey),
            ["webhooks"] = J(await store.GetWebhookStatusesAsync(default), r => r.Id),
            ["journal"] = J((await store.QueryEventsAsync(null, null, null, null, null, int.MaxValue, default)).Select(e => { e.Id = 0; return e; }), e => e.IdempotencyKey)
        };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
