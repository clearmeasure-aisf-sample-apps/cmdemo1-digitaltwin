using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalTwin.Application.Services;
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

/// <summary>The daily reconciliation compares the twin with Octopus (stubbed HTTP) and reports drift via API.</summary>
[Collection(SqlServerTests.Name)]
public sealed class ReconciliationTests(SqlServerFixture sql)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task OctopusDeployedVersionsAndReleasesAreComparedWithTheTwin()
    {
        var octopus = new StubOctopus();
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?> { ["OCTOPUS_API_KEY"] = "test-key" },
            services => services.AddHttpClient("octopus").ConfigurePrimaryHttpMessageHandler(() => octopus));
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("uat", "succeeded", 60, 63)], EventSourceKind.Octopus, default);
        }));

        var report = await (await client.PostAsync("/api/admin/reconciliation/run", null)).Content.ReadFromJsonAsync<ReconciliationReport>(Json);

        var deployed = Assert.Single(report!.Checks, c => c is { Source: "Octopus", Name: "deployed versions" });
        Assert.Equal(2, deployed.SourceCount);
        Assert.Equal(1, deployed.TwinCount);
        Assert.Equal(["ui@uat: Octopus 2.4.2665, twin 2.4.2664"], deployed.MissingInTwin);
        var releases = Assert.Single(report.Checks, c => c is { Source: "Octopus", Name: "recent releases" });
        Assert.Equal(["Releases-52230"], releases.MissingInTwin);
        Assert.True(report.DriftCount >= 2);
        // GitHub has no token in tests: its checks report errors instead of drift.
        Assert.All(report.Checks.Where(c => c.Source == "GitHub"), c => Assert.NotNull(c.Error));

        var latest = await client.GetFromJsonAsync<ReconciliationReport>("/api/reconciliation", Json);
        Assert.Equal(report.FinishedAt, latest!.FinishedAt);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/reconciliation/status");
        Assert.Equal(report.DriftCount, status.GetProperty("driftCount").GetInt32());
    }

    private sealed class StubOctopus : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/api/Spaces-355/dashboard" => """
                    {"Projects":[{"Id":"Projects-1","Name":"cmdemo1-ui","Slug":"cmdemo1-ui"}],
                     "Environments":[{"Id":"Environments-1","Name":"tdd"},{"Id":"Environments-2","Name":"uat"}],
                     "Items":[{"ProjectId":"Projects-1","EnvironmentId":"Environments-1","ReleaseVersion":"2.4.2664","State":"Success","IsCurrent":true},
                              {"ProjectId":"Projects-1","EnvironmentId":"Environments-2","ReleaseVersion":"2.4.2665","State":"Success","IsCurrent":true}]}
                    """,
                "/api/Spaces-355/releases" => """{"Items":[{"Id":"Releases-52228"},{"Id":"Releases-52230"}]}""",
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
