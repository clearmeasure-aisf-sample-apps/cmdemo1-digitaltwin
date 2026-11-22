using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Application.Intake;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace DigitalTwin.Tests;

[Collection(SqlServerTests.Name)]
public sealed class IntakeDurabilityTests(SqlServerFixture sql)
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(60);
    private const string PingPayload = """{"zen":"Keep it awesome","repository":{"name":"cmdemo1-digitaltwin","full_name":"clearmeasure-aisf-sample-apps/cmdemo1-digitaltwin"},"sender":{"login":"tester"}}""";

    [Fact]
    public async Task DuplicateDeliveriesAndWebhookPollOverlapAreCapturedButAppliedOnce()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var publisher = app.Services.GetRequiredService<IIntakePublisher>();

        await app.Services.TrackActivity().Timeout(ProcessingTimeout).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            // GitHub redelivers the same delivery id twice
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "dup-1", PingPayload))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "dup-1", PingPayload))).StatusCode);

            // A backstop poller observes the same occurrence (same idempotency key)
            var polled = new ObservedEvent
            {
                EventId = "dup-1",
                IdempotencyKey = "github:dup-1",
                Source = EventSourceKind.GitHub,
                SourceEventId = "dup-1",
                EventType = "dev.cdevents.repository.modified",
                SubjectType = "repository",
                SubjectId = "cmdemo1-digitaltwin",
                Predicate = "pinged",
                OccurredAt = DateTimeOffset.UtcNow,
                IngestedAt = DateTimeOffset.UtcNow,
                Channel = IngestionChannel.Poll,
                Summary = "poll overlap"
            };
            await publisher.CaptureAsync(RawIntakeEvent.ForObservedBatch([polled], EventSourceKind.GitHub, IngestionChannel.Poll, DateTimeOffset.UtcNow), CancellationToken.None);
        }));

        Assert.Equal(3, await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events"));
        Assert.Equal(3, await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events WHERE processed_at IS NOT NULL"));
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE IdempotencyKey = 'github:dup-1'"));

        var status = await client.GetFromJsonAsync<IntakeQueueStatus>("/api/intake", JsonWeb);
        Assert.NotNull(status);
        Assert.Equal(0, status.Depth);
        Assert.Equal(3, status.CapturedTotal);
        Assert.Equal(3, status.ProcessedTotal);
    }

    [Fact]
    public async Task EventsCapturedWhileProcessorIsDownAreProcessedWhenItStarts()
    {
        var db = await sql.CreateDatabaseAsync();
        var sqlTransport = new Dictionary<string, string?> { ["Twin:Messaging:Transport"] = "SqlServer" };

        // Intake-only host: webhooks are acknowledged after durable capture, nothing is processed.
        await using (var intakeOnly = new TwinAppFactory(db, new Dictionary<string, string?>(sqlTransport) { ["Twin:Messaging:ProcessorEnabled"] = "false" }))
        {
            using var client = intakeOnly.CreateAuthedClient();
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "down-1", PingPayload))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TwinAppFactory.OctopusWebhook("Events-down-2"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TwinAppFactory.AzureWebhook("az-down-3"))).StatusCode);

            await TwinAppFactory.WaitUntilAsync(
                async () => await intakeOnly.ScalarAsync("SELECT COUNT(*) FROM wolverine.wolverine_queue_twin_intake") == 3,
                ProcessingTimeout,
                "all three envelopes are sitting in the SQL Server queue");

            var status = await client.GetFromJsonAsync<IntakeQueueStatus>("/api/intake", JsonWeb);
            Assert.Equal(3, status!.Depth);
            Assert.NotNull(status.OldestPendingAgeSeconds);
            Assert.Equal(0, await intakeOnly.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents"));
        }

        // Processor comes up later against the same database and drains the backlog.
        await using var processor = new TwinAppFactory(db, sqlTransport);
        using var processorClient = processor.CreateAuthedClient();
        await TwinAppFactory.WaitUntilAsync(
            async () => await processor.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events WHERE processed_at IS NULL") == 0,
            ProcessingTimeout,
            "the processor applies the backlog");

        Assert.Equal(3, await processor.ScalarAsync(
            "SELECT COUNT(*) FROM ObservedEvents WHERE IdempotencyKey IN ('github:down-1', 'octopus:Events-down-2', 'azure:az-down-3')"));
        var after = await processorClient.GetFromJsonAsync<IntakeQueueStatus>("/api/intake", JsonWeb);
        Assert.Equal(0, after!.Depth);
        Assert.Equal("SqlServer", after.Transport);
    }

    [Fact]
    public async Task FailingEventIsRetriedThenDeadLetteredAndReplayedAfterTheFix()
    {
        var faults = new FaultInjector();
        await using var app = new TwinAppFactory(
            await sql.CreateDatabaseAsync(),
            configureServices: services => services.AddSingleton<IIntakeApplyInterceptor>(faults));
        using var client = app.CreateAuthedClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "poison-1", PingPayload))).StatusCode);

        await TwinAppFactory.WaitUntilAsync(async () =>
            {
                var s = await client.GetFromJsonAsync<IntakeQueueStatus>("/api/intake", JsonWeb);
                return s!.DeadLetterCount == 1;
            },
            ProcessingTimeout,
            "the message is dead-lettered after retries");

        Assert.Equal(4, faults.Attempts); // first try + 3 retries with backoff
        Assert.Equal(0, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE IdempotencyKey = 'github:poison-1'"));
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events WHERE dead_lettered_at IS NOT NULL AND processed_at IS NULL"));

        var deadLetters = await client.GetFromJsonAsync<List<DeadLetteredIntake>>("/api/admin/deadletters", JsonWeb);
        var dl = Assert.Single(deadLetters!);
        Assert.NotNull(dl.RawEventId);
        Assert.Contains("injected", dl.ExceptionMessage ?? string.Empty, StringComparison.Ordinal);

        faults.Enabled = false;
        var replay = await client.PostAsync("/api/admin/deadletters/replay", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        await TwinAppFactory.WaitUntilAsync(
            async () => await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE IdempotencyKey = 'github:poison-1'") == 1,
            ProcessingTimeout,
            "the replayed message is applied");
        await TwinAppFactory.WaitUntilAsync(async () =>
            {
                var s = await client.GetFromJsonAsync<IntakeQueueStatus>("/api/intake", JsonWeb);
                return s!.DeadLetterCount == 0 && s.Depth == 0;
            },
            ProcessingTimeout,
            "dead letters and depth return to zero");
    }

    [Fact]
    public async Task RebuildFromRawEventsYieldsIdenticalJournal()
    {
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync());
        using var client = app.CreateAuthedClient();
        var intake = app.Services.GetRequiredService<ObservedEventIntake>();

        await app.Services.TrackActivity().Timeout(ProcessingTimeout).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "rebuild-1", PingPayload));
            await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "rebuild-1", PingPayload));
            await client.SendAsync(TwinAppFactory.OctopusWebhook("Events-rebuild-2"));
            await client.SendAsync(TwinAppFactory.AzureWebhook("az-rebuild-3"));
            await intake.PublishAsync(
                [
                    new ObservedEvent
                    {
                        EventId = "poll-4", IdempotencyKey = "octopus:poll-4", Source = EventSourceKind.Octopus, SourceEventId = "poll-4",
                        EventType = "dev.cdevents.service.deployed", SubjectType = "deployment", SubjectId = "Deployments-4", Predicate = "deployed",
                        OccurredAt = DateTimeOffset.Parse("2026-11-21T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                        IngestedAt = DateTimeOffset.Parse("2026-11-21T04:00:05Z", System.Globalization.CultureInfo.InvariantCulture),
                        Channel = IngestionChannel.Poll, StageName = "prod", DeployableName = "ui", Outcome = EventOutcome.Success, Summary = "polled deploy"
                    }
                ],
                EventSourceKind.Octopus,
                IngestionChannel.Poll,
                CancellationToken.None);
        }));

        var store = app.Services.GetRequiredService<ITwinStore>();
        var before = Fingerprint(await store.QueryEventsAsync(null, null, null, null, null, 10_000, CancellationToken.None));
        Assert.Equal(4, before.Count);

        var response = await client.PostAsync("/api/admin/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<IntakeRebuildReport>(JsonWeb);
        Assert.Equal(4, report!.JournalEventsCleared);
        Assert.Equal(5, report.RawEventsReplayed);
        Assert.Equal(0, report.RawEventsFailed);

        var after = Fingerprint(await store.QueryEventsAsync(null, null, null, null, null, 10_000, CancellationToken.None));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task WebhookReturns503WhenCaptureCannotCommit()
    {
        await using var app = new TwinAppFactory(
            await sql.CreateDatabaseAsync(),
            configureServices: services => services.Replace(ServiceDescriptor.Singleton<IIntakePublisher, FailingPublisher>()));
        using var client = app.CreateClient();

        var response = await client.SendAsync(TwinAppFactory.GitHubWebhook("ping", "unavailable-1", PingPayload));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await app.ScalarAsync("SELECT COUNT(*) FROM twin.raw_intake_events"));
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static List<string> Fingerprint(IEnumerable<ObservedEvent> events)
        => events
            .Select(e => JsonSerializer.Serialize(new
            {
                e.EventId, e.IdempotencyKey, e.Source, e.SourceEventId, e.SpecVersion, e.EventType, e.SubjectType, e.SubjectId, e.Predicate,
                OccurredAt = e.OccurredAt.ToUniversalTime(), IngestedAt = e.IngestedAt.ToUniversalTime(), e.Channel, e.CorrelationId, e.ChainId,
                e.PipelineId, e.StageName, e.DeployableName, e.Actor, e.Outcome, e.Summary, e.SourceUrl, e.PayloadJson
            }))
            .Order(StringComparer.Ordinal)
            .ToList();

    private sealed class FaultInjector : IIntakeApplyInterceptor
    {
        private int _attempts;
        public bool Enabled { get; set; } = true;
        public int Attempts => _attempts;

        public ValueTask BeforeApplyAsync(RawIntakeEvent rawEvent, CancellationToken ct)
        {
            if (Enabled && rawEvent.DedupeKey == "github:poison-1")
            {
                Interlocked.Increment(ref _attempts);
                throw new InvalidOperationException("injected processing failure");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingPublisher : IIntakePublisher
    {
        public Task<IntakeReceipt> CaptureAsync(RawIntakeEvent rawEvent, CancellationToken ct)
            => throw new InvalidOperationException("SQL unavailable");
    }
}
