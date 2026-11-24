using System.Text.Json;
using DigitalTwin.Application.Intake;
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

[Collection(SqlServerTests.Name)]
public sealed class JournalArchiveTests(SqlServerFixture sql) : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);
    private static readonly DateTimeOffset Day3 = Day1.AddDays(2);
    private readonly string _archiveRoot = Path.Combine(Path.GetTempPath(), $"twin-journal-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_archiveRoot))
        {
            Directory.Delete(_archiveRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ArchivingAndCompactingTheJournalKeepsEveryPointInTimeAnswer()
    {
        var clock = new ManualClock(Day1.AddHours(10));
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Twin:Archive:Provider"] = "FileSystem",
            ["Twin:Archive:Path"] = _archiveRoot
        }, s => s.AddSingleton<TimeProvider>(clock));
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        var store = app.Services.GetRequiredService<ITwinStore>();

        async Task Observe(DateTimeOffset at, Func<Task> publish)
        {
            clock.Now = at;
            await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => publish()));
        }
        WorkItemSnapshot Item(string title) { var w = F.WorkItem(); w.Title = title; return w; }

        // Day 1: the work item changes twice, Azure has two resources, one drift finding appears.
        await Observe(Day1.AddHours(10), () => state.PublishAsync(StateKinds.WorkItem, [Item("v1")], EventSourceKind.GitHub, default));
        await Observe(Day1.AddHours(11), () => state.PublishSetAsync(StateKinds.AzureResource, "azure", [F.Resource("ca-a"), F.Resource("ca-b")], EventSourceKind.Azure, default));
        await Observe(Day1.AddHours(12), () => state.PublishSetAsync(StateKinds.Drift, "cmdemo1", [F.Drift()], EventSourceKind.Twin, default));
        await Observe(Day1.AddHours(14), () => state.PublishAsync(StateKinds.WorkItem, [Item("v2")], EventSourceKind.GitHub, default));
        // Day 2: the item changes again, one resource is deleted, the drift resolves.
        await Observe(Day2.AddHours(9), () => state.PublishAsync(StateKinds.WorkItem, [Item("v3")], EventSourceKind.GitHub, default));
        await Observe(Day2.AddHours(10), () => state.PublishSetAsync(StateKinds.AzureResource, "azure", [F.Resource("ca-a")], EventSourceKind.Azure, default));
        await Observe(Day2.AddHours(11), () => state.PublishSetAsync(StateKinds.Drift, "cmdemo1", Array.Empty<DriftFinding>(), EventSourceKind.Twin, default));
        // Day 3 (today): the item changes once more.
        await Observe(Day3.AddHours(8), () => state.PublishAsync(StateKinds.WorkItem, [Item("v4")], EventSourceKind.GitHub, default));

        // Lifecycle events: a failed build on a chain (kept for chain signals and DORA), a push (archived only).
        await store.AppendEventsIfNewAsync([Lifecycle("signal", "dev.cdevents.build.finished", Day1.AddHours(13), "sha:abc", EventOutcome.Failure), Lifecycle("push", "dev.cdevents.change.merged", Day1.AddHours(13), null, EventOutcome.Success)], default);
        clock.Now = Day3.AddHours(12);

        var instants = new[] { Day1.AddHours(10).AddMinutes(30), Day1.AddHours(13), Day1.AddHours(23), Day2.AddHours(9).AddMinutes(30), Day2.AddHours(12), Day3.AddHours(9) };
        var queries = app.Services.GetRequiredService<TwinQueryService>();
        var timeTravel = app.Services.GetRequiredService<TwinTimeTravel>();
        var before = new Dictionary<DateTimeOffset, string>();
        foreach (var at in instants)
        {
            before[at] = await AnswerAsync(queries, timeTravel, at);
        }
        var historyBefore = await timeTravel.GetHistoryStartAsync(default);
        var rowsBefore = await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents");

        var retention = app.Services.GetRequiredService<JournalRetentionService>();
        var report = await retention.RunAsync(olderThanDays: 0.5, default);

        Assert.Null(report.Skipped);
        Assert.Equal([DateOnly.FromDateTime(Day1.UtcDateTime), DateOnly.FromDateTime(Day2.UtcDateTime)], report.Days.Select(d => d.Day).Order());
        var archive = app.Services.GetRequiredService<IJournalArchive>();
        Assert.Equal(report.Days.Select(d => d.Day).Order(), await archive.ListDaysAsync(default));
        Assert.Equal(report.Days.Select(d => d.Day).Order(), await archive.ListCheckpointsAsync(default));
        Assert.True(report.Days.Sum(d => d.SupersededStateRemoved) >= 3);
        Assert.Equal(1, report.Days.Sum(d => d.LifecycleRemoved));
        Assert.True(await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents") < rowsBefore);
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE IdempotencyKey = 'test:signal'"));
        Assert.Equal(0, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE IdempotencyKey = 'test:push'"));
        Assert.Equal(2, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE SubjectType = 'drift'"));
        // The work item keeps its newest observation as of the boundary (v3) and today's (v4).
        Assert.Equal(2, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE SubjectType = 'work-item'"));

        foreach (var at in instants)
        {
            Assert.Equal(before[at], await AnswerAsync(queries, timeTravel, at));
        }
        Assert.Equal(historyBefore, await timeTravel.GetHistoryStartAsync(default));
        var live = await ReadModelDump.DumpAsync(store);
        Assert.Equal(live, await ReadModelDump.DumpAsync((await timeTravel.ResolveAsync(clock.Now.AddTicks(-1), default)).Model));

        Assert.Empty((await retention.RunAsync(olderThanDays: 0.5, default)).Days);

        // Part of today (before 09:36) is archived; a newer observation then arrives and the open day is merged again.
        var partial = await retention.RunAsync(olderThanDays: 0.1, default);
        Assert.Equal(DateOnly.FromDateTime(Day3.UtcDateTime), Assert.Single(partial.Days).Day);
        foreach (var at in instants)
        {
            Assert.Equal(before[at], await AnswerAsync(queries, timeTravel, at));
        }
        await Observe(Day3.AddHours(11), () => state.PublishAsync(StateKinds.WorkItem, [Item("v5")], EventSourceKind.GitHub, default));
        clock.Now = Day3.AddHours(12);
        var afterV5 = Day3.AddHours(11).AddMinutes(30);
        var answerAfterV5 = await AnswerAsync(queries, timeTravel, afterV5);
        Assert.Single((await retention.RunAsync(olderThanDays: 0.01, default)).Days);
        Assert.Equal(answerAfterV5, await AnswerAsync(queries, timeTravel, afterV5));
        foreach (var at in instants)
        {
            Assert.Equal(before[at], await AnswerAsync(queries, timeTravel, at));
        }
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE SubjectType = 'work-item'"));
        Assert.Equal(await ReadModelDump.DumpAsync(store), await ReadModelDump.DumpAsync((await timeTravel.ResolveAsync(clock.Now.AddTicks(-1), default)).Model));

        // A lifecycle event arriving late for an archived day is merged into its file.
        await store.AppendEventsIfNewAsync([Lifecycle("late", "dev.cdevents.change.merged", Day1.AddHours(15), null, EventOutcome.Success)], default);
        var late = await retention.RunAsync(olderThanDays: 0.01, default);
        var lateDay = Assert.Single(late.Days);
        Assert.Equal(("late", 1), (lateDay.Reason, lateDay.LifecycleRemoved));
        // Only the late row is read out of SQL; what the day file already holds is not fetched again.
        Assert.Equal(1, lateDay.FetchedFromSql);
        var day1File = await archive.ReadDayAsync(DateOnly.FromDateTime(Day1.UtcDateTime), default).Select(e => e.IdempotencyKey).ToListAsync();
        Assert.Contains("test:late", day1File);
        Assert.Contains("test:push", day1File);
    }

    [Fact]
    public async Task OverTheSizeBudgetEverythingButTheLastHourIsArchived()
    {
        var clock = new ManualClock(Day1.AddHours(10));
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Twin:Archive:Provider"] = "FileSystem",
            ["Twin:Archive:Path"] = _archiveRoot,
            ["Twin:JournalRetention:MaxJournalMb"] = "0"
        }, s => s.AddSingleton<TimeProvider>(clock));
        using var client = app.CreateAuthedClient();
        var state = app.Services.GetRequiredService<StateObservationPublisher>();
        foreach (var hour in new[] { 10, 11, 12 })
        {
            clock.Now = Day1.AddHours(hour);
            var item = F.WorkItem();
            item.Title = $"at {hour}";
            await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ =>
                state.PublishAsync(StateKinds.WorkItem, [item], EventSourceKind.GitHub, default)));
        }
        clock.Now = Day1.AddHours(12).AddMinutes(30);

        var report = await app.Services.GetRequiredService<JournalRetentionService>().RunAsync(olderThanDays: null, default);

        Assert.Equal("size", Assert.Single(report.Days).Reason);
        Assert.Equal(Day1.AddHours(11).AddMinutes(30), (await app.Services.GetRequiredService<IJournalArchive>().ReadMetaAsync(default))!.ArchivedThrough);
        // 10:00 is superseded by 11:00 before the boundary; 11:00 stays (newest before it) and 12:00 is in the kept hour.
        Assert.Equal(2, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE SubjectType = 'work-item'"));
    }

    private static async Task<string> AnswerAsync(TwinQueryService queries, TwinTimeTravel timeTravel, DateTimeOffset at)
    {
        var stateAt = await queries.GetStateAtAsync(at, default);
        var dump = await ReadModelDump.DumpAsync((await timeTravel.ResolveAsync(at, default)).Model);
        return JsonSerializer.Serialize(new { stateAt = stateAt with { HistoryStartsAt = null }, dump }, IntakeJson.Options);
    }

    private static ObservedEvent Lifecycle(string key, string type, DateTimeOffset at, string? chainId, EventOutcome outcome) => new()
    {
        EventId = key,
        IdempotencyKey = $"test:{key}",
        Source = EventSourceKind.GitHub,
        SourceEventId = key,
        EventType = type,
        SubjectType = "build",
        SubjectId = key,
        Predicate = "finished",
        OccurredAt = at,
        IngestedAt = at,
        Channel = IngestionChannel.Webhook,
        ChainId = chainId,
        Outcome = outcome,
        Summary = key,
        PayloadJson = "{}"
    };
}
