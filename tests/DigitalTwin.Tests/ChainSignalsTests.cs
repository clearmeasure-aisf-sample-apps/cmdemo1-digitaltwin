using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalTwin.Application.Models;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

/// <summary>Reviews, failed jobs, interventions and sign-offs attach to chain stages; Octopus config changes are listed.</summary>
[Collection(SqlServerTests.Name)]
public sealed class ChainSignalsTests(SqlServerFixture sql)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ChainStagesCarryJournalSignalsAndConfigChangesAreListed()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        const string repo = "\"repository\":{\"name\":\"cmdemo1-workorders\",\"full_name\":\"clearmeasure-aisf-sample-apps/cmdemo1-workorders\"}";

        await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await state.PublishAsync(StateKinds.WorkItem, [F.WorkItem()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.SourceChange, [F.PullRequest()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.PipelineRun, [F.Build()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("uat", "succeeded", 60, 63), F.Delivery("prod", "succeeded", 90, 95)], EventSourceKind.Octopus, default);
            await client.SendAsync(TwinAppFactory.GitHubWebhook("pull_request_review", "review-1",
                $$$"""{"action":"submitted","review":{"state":"approved","submitted_at":"2026-11-20T10:20:00Z"},"pull_request":{"number":1950},{{{repo}}},"sender":{"login":"alice"}}"""));
            await client.SendAsync(TwinAppFactory.GitHubWebhook("workflow_job", "job-1",
                $$$"""{"action":"completed","workflow_job":{"id":7,"run_id":44863242282,"name":"integration-tests","head_sha":"{{{F.Sha}}}","status":"completed","conclusion":"failure","completed_at":"2026-11-20T10:38:00Z"},{{{repo}}}}"""));
            await client.SendAsync(Octopus("Events-90", "ManualInterventionInterruptionRaised", "Deploy to prod requires manual intervention for cmdemo1-ui release 2.4.2664 to prod", "Releases-52228", "Interruptions-5", "Environments-3"));
            await client.SendAsync(Octopus("Events-91", "Modified", "Submitted interruption Sign-off with notes: checks green for cmdemo1-ui release 2.4.2664 to prod", "Releases-52228", "Interruptions-5", "Environments-3"));
            await client.SendAsync(Octopus("Events-92", "Modified", "Deployment process for project cmdemo1-ui was modified", "deploymentprocess-Projects-1", "Projects-1"));
        }));

        var chain = Assert.Single((await client.GetFromJsonAsync<ChangeChainsDto>("/api/change-chains?q=1941", Json))!.Chains);
        var signals = chain.Signals!;
        Assert.Contains(signals, s => s is { Stage: ChainStageKind.PullRequest, Kind: "review", Outcome: "Success" });
        Assert.Contains(signals, s => s is { Stage: ChainStageKind.Build, Kind: "job-failed" });
        Assert.Contains(signals, s => s is { Stage: ChainStageKind.Prod, Kind: "manual-intervention" });
        Assert.Contains(signals, s => s is { Stage: ChainStageKind.Prod, Kind: "signed-off" });

        var changes = await client.GetFromJsonAsync<List<ConfigChangeDto>>("/api/config-changes", Json);
        var change = Assert.Single(changes!);
        Assert.Equal("process", change.Kind);
        Assert.Equal("cm-bot", change.Actor);
    }

    private static HttpRequestMessage Octopus(string id, string category, string message, params string[] related)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Timestamp = "2026-11-20T11:30:00Z",
            EventType = "SubscriptionPayload",
            Payload = new { ServerUri = "https://clearmeasure.octopus.app", Event = new { Id = id, Category = category, Message = message, Occurred = "2026-11-20T11:30:00Z", Username = "cm-bot", RelatedDocumentIds = related } }
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/octopus") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Webhook-Secret", TwinAppFactory.WebhookSecret);
        return request;
    }
}
