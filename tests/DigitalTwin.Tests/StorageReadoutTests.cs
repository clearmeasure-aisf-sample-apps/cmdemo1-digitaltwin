using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

[Collection(SqlServerTests.Name)]
public sealed class StorageReadoutTests(SqlServerFixture sql)
{
    [Fact]
    public async Task TheStorageReadoutShowsDatabaseRawJournalAndTheRebuildProjection()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await state.PublishAsync(StateKinds.WorkItem, [F.WorkItem()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
            await state.PublishSetAsync(StateKinds.Drift, "cmdemo1", [F.Drift()], EventSourceKind.Twin, default);
        }));

        using var anonymous = app.CreateClient();
        var readout = await anonymous.GetFromJsonAsync<JsonElement>("/api/storage");
        var journal = readout.GetProperty("journal");
        Assert.True(journal.GetProperty("stateRows").GetInt64() >= 3);
        Assert.True(journal.GetProperty("driftRows").GetInt64() >= 1);
        Assert.Equal(journal.GetProperty("rows").GetInt64(), journal.GetProperty("stateRows").GetInt64() + journal.GetProperty("lifecycleRows").GetInt64());
        Assert.True(journal.GetProperty("sizeMb").GetDouble() > 0);
        Assert.True(readout.GetProperty("rawIntake").GetProperty("rows").GetInt64() >= 3);
        var rebuild = readout.GetProperty("rebuild");
        Assert.True(rebuild.GetProperty("projectedPeakMb").GetDouble() > readout.GetProperty("database").GetProperty("usedMb").GetDouble());
        Assert.Equal(1500, rebuild.GetProperty("budgetMb").GetDouble());
        Assert.True(rebuild.GetProperty("withinBudget").GetBoolean());

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/admin/storage?detail=true");
        var kinds = detail.GetProperty("journal").GetProperty("kinds").EnumerateArray().Select(k => k.GetProperty("kind").GetString()).ToList();
        Assert.Contains("state:work-item", kinds);
        Assert.Contains("state:drift", kinds);
    }
}
