using System.Net;
using System.Net.Http.Json;
using DigitalTwin.Application.Intake;
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

/// <summary>Rebuilds write shadow tables while live intake keeps running, then swap atomically.</summary>
[Collection(SqlServerTests.Name)]
public sealed class ShadowRebuildTests(SqlServerFixture sql)
{
    private const string Ping = """{"zen":"x","repository":{"name":"cmdemo1-workorders"}}""";

    [Fact]
    public async Task LiveIntakeKeepsFlowingDuringARebuildAndIsInTheSwappedState()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        await SeedHistoryAsync(app);
        var rebuild = app.Services.GetRequiredService<IntakeRebuildService>();

        var running = rebuild.StartOrJoin();
        await TwinAppFactory.WaitUntilAsync(() => Task.FromResult(rebuild.GetStatus().Phase is "replaying history" or "catching up"),
            TimeSpan.FromSeconds(60), "the rebuild is replaying");

        // Captured and applied live while the rebuild runs: no pause, no deferral.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "during-shadow-1", Ping))).StatusCode);
        await TwinAppFactory.WaitUntilAsync(
            async () => await app.ScalarAsync("SELECT COUNT(*) FROM dbo.ObservedEvents WHERE IdempotencyKey = 'github:during-shadow-1'") == 1,
            TimeSpan.FromSeconds(15), "the webhook is applied live during the rebuild");
        Assert.True(rebuild.IsRunning || running.IsCompleted);

        var report = await running;
        Assert.True(report.Swapped, string.Join("; ", report.Differences));
        Assert.True(report.Identical, string.Join("; ", report.Differences));
        Assert.True(report.LivePausedMilliseconds < 10_000, $"live paused {report.LivePausedMilliseconds} ms");
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM dbo.ObservedEvents WHERE IdempotencyKey = 'github:during-shadow-1'"));
        Assert.Equal(450, (await app.Services.GetRequiredService<ITwinStore>().GetPipelineRunsAsync(int.MaxValue, default)).Count);
        Assert.Equal(0, await ShadowTableCountAsync(app));
        // The swapped-in journal keeps the filtered index the dashboard's recent-events query reads.
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ObservedEvents_Recent' AND object_id = OBJECT_ID('dbo.ObservedEvents') AND has_filter = 1"));
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ObservedEvents_TypeTime' AND object_id = OBJECT_ID('dbo.ObservedEvents')"));
    }

    [Fact]
    public async Task CancellingARebuildDiscardsTheShadowAndKeepsLiveState()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        await SeedHistoryAsync(app);
        var rebuild = app.Services.GetRequiredService<IntakeRebuildService>();
        var before = await app.Services.GetRequiredService<ITwinStore>().GetReadModelFingerprintAsync(default);

        var running = rebuild.StartOrJoin();
        await TwinAppFactory.WaitUntilAsync(() => Task.FromResult(rebuild.GetStatus().Phase is not null), TimeSpan.FromSeconds(30), "the rebuild started");
        Assert.True(rebuild.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(before, await app.Services.GetRequiredService<ITwinStore>().GetReadModelFingerprintAsync(default));
        Assert.Equal(0, await ShadowTableCountAsync(app));
        Assert.Contains("Cancel", rebuild.GetStatus().LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AShadowLeftByAnInterruptedRebuildIsDroppedAtStartup()
    {
        var connectionString = await sql.CreateDatabaseAsync();
        await using (var first = new TwinAppFactory(connectionString))
        {
            await first.Services.GetRequiredService<IRebuildTarget>().CreateAsync(default);
            Assert.True(await ShadowTableCountAsync(first) > 0);
        }
        await using var second = new TwinAppFactory(connectionString);
        using var client = second.CreateAuthedClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(0, await ShadowTableCountAsync(second));
    }

    [Fact]
    public async Task ARebuildCompactsTheShadowJournalToTheArchiveBoundaryAndSwapsIdentically()
    {
        var archiveRoot = Path.Combine(Path.GetTempPath(), $"twin-rebuild-archive-{Guid.NewGuid():N}");
        try
        {
            await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
            {
                ["Twin:Archive:Provider"] = "FileSystem",
                ["Twin:Archive:Path"] = archiveRoot
            });
            using var client = app.CreateAuthedClient();
            await SeedHistoryAsync(app);
            // The same work item observed five times: four of those observations are superseded.
            var state = app.Services.GetRequiredService<StateObservationPublisher>();
            for (var i = 0; i < 5; i++)
            {
                var item = F.WorkItem();
                item.Title = $"revision {i}";
                await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ =>
                    state.PublishAsync(StateKinds.WorkItem, [item], EventSourceKind.GitHub, default)));
            }

            var journal = await app.Services.GetRequiredService<JournalRetentionService>().RunAsync(olderThanDays: 0, default);
            Assert.True(journal.Days.Sum(d => d.SupersededStateRemoved) >= 4);
            var compactedRows = await app.ScalarAsync("SELECT COUNT(*) FROM dbo.ObservedEvents");

            var report = await app.Services.GetRequiredService<IntakeRebuildService>().StartOrJoin();

            Assert.True(report.Swapped, string.Join("; ", report.Differences));
            Assert.True(report.ReadModelsIdentical, string.Join("; ", report.Differences));
            Assert.True(report.Identical, string.Join("; ", report.Differences));
            Assert.NotNull(report.JournalArchivedThrough);
            Assert.True(report.ShadowCompaction!.SupersededStateRows >= 4);
            Assert.Equal(compactedRows, await app.ScalarAsync("SELECT COUNT(*) FROM dbo.ObservedEvents"));
            Assert.True(report.LivePausedMilliseconds < 10_000, $"live paused {report.LivePausedMilliseconds} ms");
            Assert.True(report.StepSeconds!.ContainsKey("journalFingerprints"));
        }
        finally
        {
            if (Directory.Exists(archiveRoot))
            {
                Directory.Delete(archiveRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ARebuildThatWouldExceedThePeakBudgetIsRefused()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?> { ["Twin:Storage:RebuildPeakBudgetMb"] = "1" });
        using var client = app.CreateAuthedClient();

        var response = await client.PostAsync("/api/admin/rebuild?wait=false", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("over the 1 MB budget", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(app.Services.GetRequiredService<IntakeRebuildService>().IsRunning);
    }

    private static async Task SeedHistoryAsync(TwinAppFactory app)
    {
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        var runs = Enumerable.Range(1, 450).Select(i => { var r = F.Build(); r.Id = $"cmdemo1-workorders:{i}"; r.RunId = i; return r; }).ToList();
        await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(120)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await state.PublishAsync(StateKinds.PipelineRun, runs, EventSourceKind.GitHub, default);
            await state.PublishAsync(StateKinds.Delivery, [F.Delivery("tdd", "succeeded", 44, 47)], EventSourceKind.Octopus, default);
            for (var i = 0; i < 40; i++)
            {
                await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", $"history-{i}", Ping));
            }
        }));
    }

    private static Task<long> ShadowTableCountAsync(TwinAppFactory app)
        => app.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('shadow')");
}
