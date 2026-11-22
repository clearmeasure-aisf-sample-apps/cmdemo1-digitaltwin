using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalTwin.Application.Models;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

/// <summary>Change chains built from the fixed change: issue #1941 -> PR #1950 -> build 2.4.2664 -> RC -> Releases-52228 -> tdd/uat/prod.</summary>
public sealed class ChangeChainTests
{
    private static readonly DateTimeOffset Now = F.T0.AddHours(3);

    private static ChangeChainSources Sources(
        IEnumerable<DeliveryExecutionSnapshot>? deliveries = null,
        IEnumerable<SourceChangeSnapshot>? prs = null,
        IEnumerable<PipelineRunSnapshot>? runs = null,
        IEnumerable<WorkItemSnapshot>? workItems = null,
        bool withRelease = true)
        => new(
            (workItems ?? [F.WorkItem()]).ToList(),
            (prs ?? [F.PullRequest()]).ToList(),
            (runs ?? [F.Build()]).ToList(),
            [F.Artifact()],
            withRelease ? [F.Release()] : [],
            (deliveries ?? [F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("uat", "succeeded", 60, 63), F.Delivery("prod", "succeeded", 90, 95)]).ToList());

    [Fact]
    public void DeliveredChangeLinksEveryStageWithTimings()
    {
        var chain = Assert.Single(ChangeChainBuilder.Build(Sources(), Now));

        Assert.Equal("ui@2.4.2664", chain.ChainId);
        Assert.Equal(ChainOutcome.Delivered, chain.Outcome);
        Assert.Equal(F.Sha, chain.CommitSha);
        Assert.Equal(1941, chain.WorkItemNumber);
        Assert.Equal(1950, chain.PullRequestNumber);
        Assert.Equal("Releases-52228", chain.ReleaseId);
        Assert.All(chain.Stages, s => Assert.Equal(ChainStageStatus.Succeeded, s.Status));
        Assert.Equal(
            [ChainStageKind.WorkItem, ChainStageKind.PullRequest, ChainStageKind.Build, ChainStageKind.ReleaseCandidate, ChainStageKind.OctopusRelease, ChainStageKind.Tdd, ChainStageKind.Uat, ChainStageKind.Prod],
            chain.Stages.Select(s => s.Kind));
        // work 0->5, PR 5->30, build 31->41, RC 41->42, release 42->43, tdd 44->47, uat 60->63, prod 90->95 (minutes after T0)
        Assert.Equal([5, 25, 10, 1, 1, 3, 3, 5], chain.Stages.Select(s => s.DurationMinutes ?? -1));
        Assert.Equal(1.08, chain.LeadTimeHours);
        Assert.Equal(1.58, chain.CycleTimeHours);
        Assert.Contains($"sha:{F.Sha}", chain.CorrelationIds);
        Assert.Contains("Releases-52228", chain.CorrelationIds);
    }

    [Theory]
    [InlineData("1941")]
    [InlineData("#1950")]
    [InlineData("9d4a431")]
    [InlineData("2.4.2664")]
    [InlineData("Releases-52228")]
    [InlineData("Deployments-prod")]
    [InlineData("ui@2.4.2664")]
    public void ChainIsFoundByIssuePrCommitVersionReleaseOrDeployment(string query)
    {
        var chains = ChangeChainBuilder.Build(Sources(), Now);
        Assert.Equal("ui@2.4.2664", Assert.Single(ChangeChainBuilder.Find(chains, query)).ChainId);
    }

    [Fact]
    public void FailedDeploymentIsTheFailedStageAndRetriesAreCounted()
    {
        var chain = Assert.Single(ChangeChainBuilder.Build(Sources(
        [
            F.Delivery("tdd", "succeeded", 44, 47),
            WithId(F.Delivery("uat", "failed", 50, 52), "Deployments-uat-1"),
            F.Delivery("uat", "succeeded", 60, 63),
            F.Delivery("prod", "failed", 90, 92)
        ]), Now));

        Assert.Equal(ChainOutcome.Failed, chain.Outcome);
        Assert.Equal(ChainStageKind.Prod, chain.FailedStage);
        var uat = chain.Stages.Single(s => s.Kind == ChainStageKind.Uat);
        Assert.Equal(ChainStageStatus.Succeeded, uat.Status);
        Assert.Equal((2, 1), (uat.Attempts, uat.FailedAttempts));
        Assert.Null(chain.LeadTimeHours);
    }

    [Fact]
    public void ManualInterventionMakesTheChainWaitAtThatStage()
    {
        var waiting = F.Delivery("prod", "executing", 90, null);
        waiting.HasPendingInterruption = true;
        waiting.WaitsForTitle = "Approve production";
        var chain = Assert.Single(ChangeChainBuilder.Build(Sources([F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("uat", "succeeded", 60, 63), waiting]), Now));

        Assert.Equal(ChainOutcome.Waiting, chain.Outcome);
        Assert.Equal(ChainStageKind.Prod, chain.CurrentStage);
        Assert.Equal("waiting: Approve production", chain.Stages[^1].Detail);
    }

    [Fact]
    public void ReleaseCandidateBuiltFromAnOpenPullRequestBranchLinksThePrAndIssue()
    {
        var pr = F.PullRequest();
        pr.State = "open";
        pr.MergeCommitSha = null;
        pr.MergedAt = null;
        pr.LinkedIssueNumber = null;
        var build = F.Build();
        build.Branch = pr.HeadBranch;
        build.HeadSha = "a0cf134873aa";

        var chain = Assert.Single(ChangeChainBuilder.Build(Sources(prs: [pr], runs: [build], deliveries: [F.Delivery("tdd", "executing", 44, null)], withRelease: false), Now));

        Assert.Equal(1950, chain.PullRequestNumber);
        Assert.Equal(1941, chain.WorkItemNumber);
        Assert.Equal(ChainStageStatus.Running, chain.Stages.Single(s => s.Kind == ChainStageKind.PullRequest).Status);
        Assert.Equal(ChainStageStatus.Running, chain.Stages.Single(s => s.Kind == ChainStageKind.Tdd).Status);
        Assert.Equal(ChainOutcome.InProgress, chain.Outcome);
    }

    [Fact]
    public void OpenPullRequestWithoutBuildFormsItsOwnChainAndObserverPipelinesAreExcluded()
    {
        var pr = new SourceChangeSnapshot
        {
            Id = "cmdemo1-workorders#1962", Repository = "cmdemo1-workorders", Number = 1962, Title = "ChurchMonth completion window", State = "open",
            HeadBranch = "cm-ai-ops-bot/1946-churchmonth-completion-window", PipelineId = "app-ui", CreatedAt = Now.AddHours(-1), UpdatedAt = Now.AddMinutes(-5)
        };
        var issue = F.WorkItem();
        issue.Id = "cmdemo1-workorders#1946";
        issue.Number = 1946;
        var twinBuild = F.Build();
        twinBuild.Id = "cmdemo1-digitaltwin:1";
        twinBuild.PipelineId = "twin-observer";
        twinBuild.Version = "1.0.26";

        var chains = ChangeChainBuilder.Build(Sources(prs: [F.PullRequest(), pr], runs: [F.Build(), twinBuild], workItems: [F.WorkItem(), issue]), Now);

        Assert.Equal(2, chains.Count);
        var prChain = Assert.Single(chains, c => c.ChainId == "pr:cmdemo1-workorders#1962");
        Assert.Equal(1946, prChain.WorkItemNumber);
        Assert.Equal(ChainStageKind.PullRequest, prChain.CurrentStage);
        Assert.DoesNotContain(chains, c => c.PipelineId == "twin-observer");
    }

    private static DeliveryExecutionSnapshot WithId(DeliveryExecutionSnapshot d, string id)
    {
        d.DeploymentId = id;
        return d;
    }
}

/// <summary>Change chains through the API and MCP, from state observations plus a real webhook in the chain's timeline.</summary>
[Collection(SqlServerTests.Name)]
public sealed class ChangeChainApiTests(SqlServerFixture sql)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ChangeChainIsServedByApiMcpAndDashboard()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        var push = $$$"""{"ref":"refs/heads/master","after":"{{{F.Sha}}}","repository":{"name":"cmdemo1-workorders","full_name":"clearmeasure-aisf-sample-apps/cmdemo1-workorders"},"head_commit":{"id":"{{{F.Sha}}}","message":"Add DST tests (#1950)","timestamp":"2026-11-20T10:30:00Z"},"pusher":{"name":"cm-ai-ops-bot"}}""";

        await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await state.PublishAsync(StateKinds.WorkItem, [F.WorkItem()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.SourceChange, [F.PullRequest()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.PipelineRun, [F.Build()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Artifact, [F.Artifact()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("uat", "succeeded", 60, 63), F.Delivery("prod", "failed", 90, 92)], EventSourceKind.Octopus, default);
            await client.SendAsync(TwinAppFactory.GitHubWebhook("push", "chain-push-1", push));
        }));

        var result = await client.GetFromJsonAsync<ChangeChainsDto>("/api/change-chains?q=1941", Json);
        var chain = Assert.Single(result!.Chains);
        Assert.Equal("ui@2.4.2664", chain.ChainId);
        Assert.Equal(ChainOutcome.Failed, chain.Outcome);
        Assert.Equal(ChainStageKind.Prod, chain.FailedStage);
        var timeline = Assert.Single(result.TimelineEvents);
        Assert.Equal($"sha:{F.Sha}", timeline.ChainId);

        var mcp = JsonSerializer.Deserialize<ChangeChainsDto>(await McpCallAsync(client, "get_change_chain", new { query = F.Sha[..7] }), Json)!;
        Assert.Equal("ui@2.4.2664", Assert.Single(mcp.Chains).ChainId);

        var dashboard = await client.GetFromJsonAsync<DashboardStateDto>("/api/dashboard/state", Json);
        Assert.Equal("ui@2.4.2664", Assert.Single(dashboard!.ChangeChains!).ChainId);
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
}
