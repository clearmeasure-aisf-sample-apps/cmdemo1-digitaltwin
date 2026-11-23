using System.Net;
using System.Text;
using DigitalTwin.Core.Ports;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace DigitalTwin.Tests;

[Collection(SqlServerTests.Name)]
public sealed class WorkItemSyncTests(SqlServerFixture sql)
{
    private const string Board = """
        {"data":{"node":{"items":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"fieldValueByName":null,"content":{
          "number":319,"title":"Bot work","url":"https://github.com/o/cmdemo1-dashboard/issues/319","state":"OPEN",
          "createdAt":"2026-11-20T10:00:00Z","closedAt":null,"updatedAt":"2026-11-23T10:00:00Z","repository":{"name":"cmdemo1-dashboard"},
          "author":{"login":"cm-ai-ops-bot"},"assignees":{"nodes":[]}}}]}}}}
        """;

    private const string Issues = """
        [{"number":319,"title":"Bot work","html_url":"https://github.com/o/cmdemo1-dashboard/issues/319","state":"open",
          "created_at":"2026-11-20T10:00:00Z","closed_at":null,"updated_at":"2026-11-23T10:00:00Z","user":{"login":"cm-ai-ops-bot"},"assignee":null}]
        """;

    [Fact]
    public async Task TheBoardAndTheIssueSweepClassifyAnItemTheSameWaySoItDoesNotFlap()
    {
        var handler = new GitHubStub();
        await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?> { ["Twin:GitHubToken"] = "test-token" },
            services => services.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => handler));
        using var client = app.CreateAuthedClient();
        var sync = app.Services.GetRequiredService<IGitHubSyncService>();

        // First run: the issue sweep is due and runs with the board; later runs are board-only until the sweep interval passes.
        for (var run = 0; run < 3; run++)
        {
            await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => sync.SyncBoardOnlyAsync(default)));
        }

        Assert.True(handler.IssueCalls >= 1);
        Assert.True(handler.BoardCalls >= 3);
        var item = (await app.Services.GetRequiredService<ITwinStore>().GetWorkItemsAsync(default)).Single(w => w.Id == "cmdemo1-dashboard#319");
        Assert.Equal(("bot", "cm-ai-ops-bot"), (item.WorkerKind, item.Agent));
        Assert.Equal(1, await app.ScalarAsync("SELECT COUNT(*) FROM ObservedEvents WHERE EventType = 'twin.state.observed' AND SubjectType = 'work-item' AND SubjectId = 'cmdemo1-dashboard#319'"));
    }

    private sealed class GitHubStub : HttpMessageHandler
    {
        public int BoardCalls;
        public int IssueCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string? body = null;
            if (path.EndsWith("/graphql", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref BoardCalls);
                body = Board;
            }
            else if (path.EndsWith("/cmdemo1-dashboard/issues", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref IssueCalls);
                body = Issues;
            }
            else if (path.EndsWith("/issues", StringComparison.Ordinal))
            {
                body = "[]";
            }
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
