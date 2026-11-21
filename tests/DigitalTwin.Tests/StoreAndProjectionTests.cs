using DigitalTwin.Core.Ports;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;
using DigitalTwin.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DigitalTwin.Tests;

public sealed class StoreAndProjectionTests : IAsyncLifetime, IAsyncDisposable
{
    private SqliteConnection _connection = null!;
    private TestDbFactory _factory = null!;
    private EfTwinStore _store = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TwinDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbFactory(options);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        _store = new EfTwinStore(_factory);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AppendEventDeduplicatesByIdempotencyKey()
    {
        var ev1 = new ObservedEvent
        {
            EventId = "ev-1",
            IdempotencyKey = "github:delivery-abc",
            Source = EventSourceKind.GitHub,
            SourceEventId = "delivery-abc",
            EventType = "dev.cdevents.repository.modified",
            SubjectType = "branch",
            SubjectId = "cmdemo1-workorders:master",
            Predicate = "pushed",
            OccurredAt = DateTimeOffset.UtcNow,
            IngestedAt = DateTimeOffset.UtcNow,
            Summary = "Push 1"
        };

        var ev2 = new ObservedEvent
        {
            EventId = "ev-2",
            IdempotencyKey = "github:delivery-abc",
            Source = EventSourceKind.GitHub,
            SourceEventId = "delivery-abc",
            EventType = "dev.cdevents.repository.modified",
            SubjectType = "branch",
            SubjectId = "cmdemo1-workorders:master",
            Predicate = "pushed",
            OccurredAt = DateTimeOffset.UtcNow,
            IngestedAt = DateTimeOffset.UtcNow,
            Summary = "Duplicate Push 1"
        };

        Assert.True(await _store.AppendEventIfNewAsync(ev1, CancellationToken.None));
        Assert.False(await _store.AppendEventIfNewAsync(ev2, CancellationToken.None));
        var existing = await _store.FindExistingIdempotencyKeysAsync(["github:delivery-abc", "github:unknown"], CancellationToken.None);
        Assert.Equal(["github:delivery-abc"], existing);

        var all = await _store.QueryEventsAsync(null, null, null, null, null, 50, CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task DuplicateKeysKeepTheEarliestObservationRegardlessOfProcessingOrder()
    {
        static ObservedEvent Observation(DateTimeOffset ingestedAt, string summary) => new()
        {
            EventId = "redelivered-1",
            IdempotencyKey = "github:redelivered-1",
            Source = EventSourceKind.GitHub,
            SourceEventId = "redelivered-1",
            EventType = "dev.cdevents.repository.modified",
            SubjectType = "repository",
            SubjectId = "cmdemo1-digitaltwin",
            Predicate = "pinged",
            OccurredAt = ingestedAt,
            IngestedAt = ingestedAt,
            Summary = summary
        };

        var first = DateTimeOffset.Parse("2026-11-21T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var later = first.AddSeconds(3);

        // Later redelivery processed first, original second
        Assert.Single(await _store.AppendEventsReturnNewAsync([Observation(later, "redelivery")], CancellationToken.None));
        Assert.Empty(await _store.AppendEventsReturnNewAsync([Observation(first, "original")], CancellationToken.None));

        var stored = Assert.Single(await _store.QueryEventsAsync(null, null, null, null, null, 10, CancellationToken.None));
        Assert.Equal("original", stored.Summary);
        Assert.Equal(first, stored.IngestedAt);

        // Opposite order converges on the same row
        await _store.ClearEventJournalAsync(CancellationToken.None);
        await _store.AppendEventsReturnNewAsync([Observation(first, "original")], CancellationToken.None);
        await _store.AppendEventsReturnNewAsync([Observation(later, "redelivery")], CancellationToken.None);
        Assert.Equal("original", Assert.Single(await _store.QueryEventsAsync(null, null, null, null, null, 10, CancellationToken.None)).Summary);
    }

    [Fact]
    public async Task ExplainItemCorrelatesIssuePrBuildReleaseDeploymentAndWorkload()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertWorkItemsAsync([
            new WorkItemSnapshot
            {
                Id = "cmdemo1-workorders#1941",
                Repository = "cmdemo1-workorders",
                Number = 1941,
                Title = "Add DST tests",
                State = "closed",
                Column = "UAT",
                ReleaseCandidateVersion = "2.4.2664",
                UpdatedAt = now
            }
        ], CancellationToken.None);

        await _store.UpsertSourceChangesAsync([
            new SourceChangeSnapshot
            {
                Id = "cmdemo1-workorders#1941",
                Repository = "cmdemo1-workorders",
                Number = 1941,
                Title = "Add DST tests (#1941)",
                State = "merged",
                LinkedIssueNumber = 1941,
                MergeCommitSha = "9d4a43165eb9d090ab0847bbb4cc6398f1d29699",
                PipelineId = "app-ui",
                UpdatedAt = now
            }
        ], CancellationToken.None);

        await _store.UpsertPipelineRunsAsync([
            new PipelineRunSnapshot
            {
                Id = "cmdemo1-workorders:44863242282",
                RunId = 44863242282,
                RunNumber = 2664,
                Repository = "cmdemo1-workorders",
                PipelineId = "app-ui",
                WorkflowName = "Build",
                DisplayTitle = "2.4.2664",
                Version = "2.4.2664",
                HeadSha = "9d4a43165eb9d090ab0847bbb4cc6398f1d29699",
                Status = "completed",
                Conclusion = "success",
                UpdatedAt = now
            }
        ], CancellationToken.None);

        await _store.UpsertReleasesAsync([
            new ReleaseSnapshot
            {
                ReleaseId = "Releases-52228",
                ProjectSlug = "cmdemo1-ui",
                DeployableName = "ui",
                PipelineId = "app-ui",
                Version = "2.4.2664",
                GitCommitSha = "9d4a43165eb9d090ab0847bbb4cc6398f1d29699",
                CreatedAt = now
            }
        ], CancellationToken.None);

        await _store.UpsertDeliveriesAsync([
            new DeliveryExecutionSnapshot
            {
                DeploymentId = "Deployments-58839",
                TaskId = "ServerTasks-12304684",
                ProjectSlug = "cmdemo1-ui",
                DeployableName = "ui",
                PipelineId = "app-ui",
                StageName = "uat",
                ReleaseVersion = "2.4.2664",
                State = "succeeded",
                QueuedAt = now
            }
        ], CancellationToken.None);

        await _store.ReplaceWorkloadsAsync([
            new WorkloadRevisionSnapshot
            {
                Id = "uat:ui",
                StageName = "uat",
                DeployableName = "ui",
                ResourceName = "ca-cmdemo1-uat-ui-fccv",
                RunningVersion = "2.4.2664",
                ActiveRevisionName = "ca-cmdemo1-uat-ui-fccv--0000155"
            }
        ], CancellationToken.None);

        var queries = new TwinQueryService(_store, new StubRateProvider(), new StubIntakeMonitor(), TimeProvider.System);
        var explanation = await queries.ExplainItemAsync("1941", CancellationToken.None);

        Assert.True(explanation.Found);
        Assert.NotNull(explanation.WorkItem);
        Assert.Single(explanation.PullRequests);
        Assert.Single(explanation.PipelineRuns);
        Assert.Single(explanation.Releases);
        Assert.Single(explanation.Deliveries);
        Assert.Single(explanation.Workloads);
    }

    private sealed class TestDbFactory(DbContextOptions<TwinDbContext> options) : IDbContextFactory<TwinDbContext>
    {
        public TwinDbContext CreateDbContext() => new(options);
    }

    private sealed class StubIntakeMonitor : IIntakeQueueMonitor
    {
        public Task<IntakeQueueStatus> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new IntakeQueueStatus("LocalDurable", true, 0, null, null, 0, 0, 0, null, null, 0, 0, null));
    }

    private sealed class StubRateProvider : IGitHubRateLimitProvider
    {
        public GitHubRateLimitStatus GetStatus() => new(5000, 4800, 200, DateTimeOffset.UtcNow.AddHours(1), 5000, 4900, DateTimeOffset.UtcNow.AddHours(1), "normal", 0, 0, 0, 0, 0, 1500);
        public bool IsRestPaused(out string reason) { reason = string.Empty; return false; }
        public bool IsRestBackoff() => false;
    }
}
