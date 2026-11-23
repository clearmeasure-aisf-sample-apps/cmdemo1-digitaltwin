using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

/// <summary>DORA keys from change chains: three ui changes, the second fails in prod and the third restores it.</summary>
public sealed class ChainDoraTests
{
    // change minutes after T0: A delivered at 95; B's prod fails at 152; C restores prod at 240 (88 minutes later).
    internal static ChangeChainSources ThreeChanges()
    {
        var runs = new List<PipelineRunSnapshot>();
        var releases = new List<ReleaseSnapshot>();
        var deliveries = new List<DeliveryExecutionSnapshot>();
        void Change(string version, int offset, string prodState, int? prodFinish)
        {
            var build = F.Build();
            build.Id = $"cmdemo1-workorders:{version}";
            build.Version = version;
            build.HeadSha = $"sha{version}";
            runs.Add(build);
            var release = F.Release();
            release.ReleaseId = $"Releases-{version}";
            release.Version = version;
            release.GitCommitSha = $"sha{version}";
            release.CommittedAt = F.T0.AddMinutes(30 + offset);
            releases.Add(release);
            foreach (var (stage, start, state, finish) in new[] { ("tdd", 44, "succeeded", (int?)47), ("uat", 60, "succeeded", 63), ("prod", 90, prodState, prodFinish) })
            {
                var d = F.Delivery(stage, state, start + offset, finish is null ? null : finish + offset);
                d.DeploymentId = $"Deployments-{version}-{stage}";
                d.ReleaseVersion = version;
                deliveries.Add(d);
            }
        }
        Change("2.4.2664", 0, "succeeded", 95);
        Change("2.4.2665", 60, "failed", 92);
        Change("2.4.2666", 140, "succeeded", 100);
        return new ChangeChainSources([], [], runs, [], releases, deliveries);
    }

    [Fact]
    public void FourKeysAndBandsComeFromTheChains()
    {
        var asOf = F.T0.AddDays(1);
        var dora = ChainDora.Calculate(ChangeChainBuilder.Build(ThreeChanges(), asOf), asOf, 7);

        Assert.Equal(3, dora.Deployments);
        Assert.Equal(1, dora.FailedDeployments);
        Assert.Equal(Math.Round(2 / 7.0, 2), dora.DeploymentFrequency.Value);
        Assert.Equal("high", dora.DeploymentFrequency.Rating);
        Assert.Equal(1.12, dora.LeadTimeForChanges.Value); // median of A (65 min) and C (70 min)
        Assert.Equal("elite", dora.LeadTimeForChanges.Rating);
        Assert.Equal(33.3, dora.ChangeFailureRate.Value);
        Assert.Equal("low", dora.ChangeFailureRate.Rating);
        Assert.Equal(Math.Round(88 / 60.0, 2), dora.TimeToRestore.Value);
        Assert.Equal("high", dora.TimeToRestore.Rating);
        Assert.Equal(["ui@2.4.2665"], dora.FailedChains);
        Assert.Equal(3, Assert.Single(dora.ByDeployable).Deployments);
    }

    [Fact]
    public void AsOfBeforeTheRestoreLeavesTheFailureOpen()
    {
        var asOf = F.T0.AddMinutes(200);
        var dora = ChainDora.Calculate(ChangeChainBuilder.Build(ThreeChanges(), asOf), asOf, 7);

        Assert.Equal(2, dora.Deployments);
        Assert.Equal(50, dora.ChangeFailureRate.Value);
        Assert.Null(dora.TimeToRestore.Value);
        Assert.Equal("n/a", dora.TimeToRestore.Rating);
    }
}

[Collection(SqlServerTests.Name)]
public sealed class ChainDoraApiTests(SqlServerFixture sql)
{
    [Fact]
    public async Task DoraIsServedFromChainsWithAsOf()
    {
        var clock = new ManualClock(F.T0.AddDays(1));
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), configureServices: s => s.AddSingleton<TimeProvider>(clock));
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        var sources = ChainDoraTests.ThreeChanges();
        await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await state.PublishAsync(StateKinds.PipelineRun, sources.Runs, EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Release, sources.Releases, EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Delivery, sources.Deliveries, EventSourceKind.Octopus, default);
        }));

        var dora = await client.GetFromJsonAsync<JsonElement>("/api/dora?windowDays=7&deployable=ui");
        Assert.Equal(3, dora.GetProperty("deployments").GetInt32());
        Assert.Equal(33.3, dora.GetProperty("changeFailureRate").GetProperty("value").GetDouble());

        // As of before the state was observed, the twin knew of no deployments.
        clock.Now = F.T0.AddDays(2);
        var before = Uri.EscapeDataString(F.T0.AddHours(12).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var earlier = await client.GetFromJsonAsync<JsonElement>($"/api/dora?windowDays=7&asOf={before}");
        Assert.Equal(0, earlier.GetProperty("deployments").GetInt32());
    }
}
