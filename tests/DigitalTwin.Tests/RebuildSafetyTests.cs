using System.Net;
using System.Net.Http.Json;
using DigitalTwin.Application.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using DigitalTwin.Persistence;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

/// <summary>A rebuild must never lose live state the raw store cannot reproduce (e.g. rows written before event sourcing).</summary>
[Collection(SqlServerTests.Name)]
public sealed class RebuildSafetyTests(SqlServerFixture sql)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task RebuildRefusesToDropUnreproducibleRowsAndLateBaselineSeedingFixesIt()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        var store = app.Services.GetRequiredService<ITwinStore>();

        // A webhook-driven refresh projects state first, so projection versions exist before the baseline seeder runs.
        await Track(app, () => state.PublishAsync(StateKinds.WorkItem, [F.WorkItem()], EventSourceKind.GitHub, default));
        var legacy = F.WorkItem();
        legacy.Id = "cmdemo1-workorders#1200";
        legacy.Number = 1200;
        await ((EfTwinStore)store).UpsertWorkItemsAsync([legacy], default);

        var preflight = await client.GetFromJsonAsync<RebuildPreflightReport>("/api/admin/rebuild/preflight");
        Assert.False(preflight!.CanRebuild);
        Assert.Equal(["cmdemo1-workorders#1200"], preflight.Kinds["work-item"].MissingSample);

        var refused = await (await client.PostAsync("/api/admin/rebuild", null)).Content.ReadFromJsonAsync<IntakeRebuildReport>();
        Assert.True(refused!.Aborted);
        Assert.Equal(2, (await store.GetWorkItemsAsync(default)).Count);

        var seeder = app.Services.GetRequiredService<ProjectionBaselineSeeder>();
        await Track(app, async () => Assert.True(await seeder.SeedIfNeededAsync(default)));
        Assert.False(await seeder.SeedIfNeededAsync(default));

        var response = await client.PostAsync("/api/admin/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<IntakeRebuildReport>();
        Assert.False(report!.Aborted);
        Assert.True(report.Identical, string.Join("; ", report.Differences));
        Assert.Contains(await store.GetWorkItemsAsync(default), w => w.Number == 1200);
    }

    private static async Task Track(TwinAppFactory app, Func<Task> action)
        => await app.Services.TrackActivity().Timeout(Timeout).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => action()));
}
