using System.Net;
using System.Net.Http.Json;
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

/// <summary>Raw events older than the retention window move to daily archive files; rebuilds replay archive + SQL.</summary>
[Collection(SqlServerTests.Name)]
public sealed class RetentionArchiveTests(SqlServerFixture sql) : IDisposable
{
    private readonly string _archiveRoot = Path.Combine(Path.GetTempPath(), $"twin-archive-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task OldRawEventsAreArchivedPrunedAndStillRebuildIdentically()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Twin:Archive:Provider"] = "FileSystem",
            ["Twin:Archive:Path"] = _archiveRoot
        });
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        var store = app.Services.GetRequiredService<ITwinStore>();

        // Old history: a work item, release and deliveries plus a webhook, later backdated past the window.
        await Track(app, async () =>
        {
            await state.PublishAsync(StateKinds.WorkItem, [F.WorkItem()], EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47), F.Delivery("prod", "succeeded", 90, 95)], EventSourceKind.Octopus, default);
            await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "old-ping", """{"zen":"x","repository":{"name":"cmdemo1-workorders"}}"""));
        });
        var oldRows = await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events");
        await app.ScalarAsync("UPDATE twin.raw_intake_events SET received_at = DATEADD(day, -40, received_at); SELECT @@ROWCOUNT");

        // Recent history stays in SQL.
        await Track(app, async () =>
        {
            await state.PublishAsync(StateKinds.PipelineRun, [F.Build()], EventSourceKind.GitHub, default);
            await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "new-ping", """{"zen":"y","repository":{"name":"cmdemo1-workorders"}}"""));
        });
        var totalRows = await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events");
        // Backdating rewrote received_at, which webhook timestamps derive from: re-establish the journal baseline first.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/rebuild", null)).StatusCode);

        // An interrupted retention run: the day file was written but nothing was pruned. A rebuild must read each event once.
        var archive = app.Services.GetRequiredService<IRawIntakeArchive>();
        var rawStore = app.Services.GetRequiredService<IRawIntakeStore>();
        var oldDay = (await rawStore.GetArchivableDaysAsync(DateTimeOffset.UtcNow.AddDays(-30), default)).Single();
        await archive.WriteDayAsync(oldDay, (await rawStore.ReadProcessedDayPageAsync(oldDay, 0, 1000, default)).ToAsyncEnumerable(), default);
        await AssertRebuildIdentical(client);

        var report = await (await client.PostAsync("/api/admin/retention/run", null)).Content.ReadFromJsonAsync<RetentionReport>();
        Assert.Null(report!.Skipped);
        var day = Assert.Single(report.Days);
        Assert.Equal(oldDay, day.Day);
        Assert.Equal(oldRows, day.Archived);
        Assert.Equal(oldRows, day.Pruned);
        Assert.Equal(totalRows - oldRows, await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events"));
        Assert.Equal([oldDay], await archive.ListDaysAsync(default));

        var second = await (await client.PostAsync("/api/admin/retention/run", null)).Content.ReadFromJsonAsync<RetentionReport>();
        Assert.Empty(second!.Days);

        await AssertRebuildIdentical(client);
        Assert.Equal(2, (await store.GetDeliveriesAsync(10, default)).Count);
        Assert.Single(await store.GetWorkItemsAsync(default));
        Assert.Single(await store.GetPipelineRunsAsync(10, default));
    }

    [Fact]
    public async Task OverTheSizeBudgetRecentCompleteDaysAreArchivedButNeverToday()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Twin:Archive:Provider"] = "FileSystem",
            ["Twin:Archive:Path"] = _archiveRoot,
            ["Twin:Retention:MaxRawMb"] = "0"
        });
        using var client = app.CreateAuthedClient();
        await Track(app, () => client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "yesterday", """{"zen":"x","repository":{"name":"r"}}""")));
        await app.ScalarAsync("UPDATE twin.raw_intake_events SET received_at = DATEADD(day, -1, received_at); SELECT @@ROWCOUNT");
        await Track(app, () => client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "today", """{"zen":"y","repository":{"name":"r"}}""")));

        var report = await (await client.PostAsync("/api/admin/retention/run", null)).Content.ReadFromJsonAsync<RetentionReport>();
        var day = Assert.Single(report!.Days);
        Assert.Equal("size", day.Reason);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), day.Day);
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events"));
    }

    [Fact]
    public async Task WithoutAnArchiveNothingIsPruned()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        await Track(app, () => client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "kept", """{"zen":"x","repository":{"name":"r"}}""")));
        await app.ScalarAsync("UPDATE twin.raw_intake_events SET received_at = DATEADD(day, -40, received_at); SELECT @@ROWCOUNT");

        var report = await (await client.PostAsync("/api/admin/retention/run", null)).Content.ReadFromJsonAsync<RetentionReport>();
        Assert.NotNull(report!.Skipped);
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events"));
    }

    private static async Task AssertRebuildIdentical(HttpClient client)
    {
        var response = await client.PostAsync("/api/admin/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rebuild = await response.Content.ReadFromJsonAsync<IntakeRebuildReport>();
        Assert.False(rebuild!.Aborted, string.Join("; ", rebuild.Differences));
        Assert.True(rebuild.Identical, string.Join("; ", rebuild.Differences));
    }

    private static async Task Track(TwinAppFactory app, Func<Task> action)
        => await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => action()));

    public void Dispose()
    {
        if (Directory.Exists(_archiveRoot))
        {
            Directory.Delete(_archiveRoot, recursive: true);
        }
    }
}
