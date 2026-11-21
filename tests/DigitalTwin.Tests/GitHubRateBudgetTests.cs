using System.Net;
using System.Text;
using DigitalTwin.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalTwin.Tests;

public sealed class GitHubRateBudgetTests
{
    [Fact]
    public async Task UsesETagCache304WithoutConsumingRestQuotaAndRespectsBackoffAndPauseThresholds()
    {
        var calls = 0;
        var currentRemaining = 4500;
        var currentReset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();

        var handler = new FakeHttpMessageHandler(req =>
        {
            calls++;
            if (req.Headers.IfNoneMatch.Any(t => t.Tag == "\"etag-v1\""))
            {
                var notMod = new HttpResponseMessage(HttpStatusCode.NotModified);
                notMod.Headers.Add("X-RateLimit-Resource", "core");
                notMod.Headers.Add("X-RateLimit-Limit", "5000");
                notMod.Headers.Add("X-RateLimit-Remaining", currentRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
                notMod.Headers.Add("X-RateLimit-Used", (5000 - currentRemaining).ToString(System.Globalization.CultureInfo.InvariantCulture));
                notMod.Headers.Add("X-RateLimit-Reset", currentReset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return notMod;
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
            ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-v1\"");
            ok.Headers.Add("X-RateLimit-Resource", "core");
            ok.Headers.Add("X-RateLimit-Limit", "5000");
            ok.Headers.Add("X-RateLimit-Remaining", currentRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ok.Headers.Add("X-RateLimit-Used", (5000 - currentRemaining).ToString(System.Globalization.CultureInfo.InvariantCulture));
            ok.Headers.Add("X-RateLimit-Reset", currentReset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return ok;
        });

        var clientFactory = new StubHttpClientFactory(handler);
        var opts = Microsoft.Extensions.Options.Options.Create(new GitHubOptions { Token = "test-token" });
        var gh = new GitHubApiClient(clientFactory, opts, TimeProvider.System);

        // 1. First request -> 200 OK, caches ETag
        var first = await gh.GetApiAsync("repos/org/r1/actions/runs", bypassPause: false, CancellationToken.None);
        Assert.True(first.Success);
        Assert.False(first.FromCache304);
        Assert.Equal("{\"ok\":true}", first.Body);

        // 2. Second request with remaining=800 -> sends If-None-Match -> 304 Not Modified, enters backoff (<1000)
        currentRemaining = 800;
        var second = await gh.GetApiAsync("repos/org/r1/actions/runs", bypassPause: false, CancellationToken.None);
        Assert.True(second.Success);
        Assert.True(second.FromCache304);
        Assert.Equal("{\"ok\":true}", second.Body);
        Assert.True(gh.IsRestBackoff());
        Assert.Equal(1, gh.GetStatus().TwinCache304HitsTotal);
        Assert.Equal(1, gh.GetStatus().TwinRestCallsTotal);

        // 3. Drop remaining below 500 -> enters paused mode and skips HTTP call completely
        currentRemaining = 400;
        await gh.GetApiAsync("repos/org/r2/actions/runs", bypassPause: false, CancellationToken.None);
        Assert.True(gh.IsRestPaused(out _));
        var callsBeforePaused = calls;

        var skipped = await gh.GetApiAsync("repos/org/r1/actions/runs", bypassPause: false, CancellationToken.None);
        Assert.False(skipped.Success);
        Assert.True(skipped.RateLimited);
        Assert.Equal("{\"ok\":true}", skipped.Body); // Returns cached body safely!
        Assert.Equal(callsBeforePaused, calls); // Zero HTTP call made!
        Assert.Equal(1, gh.GetStatus().TwinCallsSkippedByQuota);
    }

    [Fact]
    public async Task GraphQlPauseDoesNotBlockCoreRestAndAutoResetsAfterWindow()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 21, 5, 50, 0, TimeSpan.Zero));
        var resetEpoch = clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();
        var restResetEpoch = clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds();

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("graphql", StringComparison.Ordinal))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.Add("X-RateLimit-Resource", "graphql");
                resp.Headers.Add("X-RateLimit-Limit", "5000");
                resp.Headers.Add("X-RateLimit-Remaining", "0");
                resp.Headers.Add("X-RateLimit-Reset", resetEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return resp;
            }

            var coreResp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
            coreResp.Headers.Add("X-RateLimit-Resource", "core");
            coreResp.Headers.Add("X-RateLimit-Limit", "5000");
            coreResp.Headers.Add("X-RateLimit-Remaining", "4999");
            coreResp.Headers.Add("X-RateLimit-Reset", restResetEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return coreResp;
        });

        var gh = new GitHubApiClient(new StubHttpClientFactory(handler), Microsoft.Extensions.Options.Options.Create(new GitHubOptions { Token = "test" }), clock);

        var gql = await gh.PostGraphQlAsync("query { viewer { login } }", null, CancellationToken.None);
        Assert.True(gql.RateLimited);
        Assert.True(gh.IsGraphQlPaused(out _));
        Assert.False(gh.IsRestPaused(out _));

        // REST calls still succeed while GraphQL is paused
        var rest = await gh.GetApiAsync("repos/org/r1/hooks", bypassPause: false, CancellationToken.None);
        Assert.True(rest.Success);
        Assert.Equal("normal", gh.GetStatus().Mode);

        // Advance past GraphQL reset window -> auto-resets GraphQlRemaining
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(gh.IsGraphQlPaused(out _));
        Assert.Equal(5000, gh.GetStatus().GraphQlRemaining);
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
