using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DigitalTwin.Application.Abstractions;
using DigitalTwin.Domain.Observation;
using DigitalTwin.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Infrastructure.Clients;

public sealed record GitHubFetchResult(
    bool Success,
    bool FromCache304,
    bool RateLimited,
    HttpStatusCode StatusCode,
    string? Body);

public sealed class GitHubApiClient(
    IHttpClientFactory httpClientFactory,
    IOptions<TwinOptions> options,
    TimeProvider timeProvider) : IGitHubRateLimitProvider
{
    private const int HourlyRestBudgetTarget = 1500;
    private const int BackoffThreshold = 1000;
    private const int PauseThreshold = 500;

    private sealed record CachedResponse(string? ETag, string Body, DateTimeOffset CachedAt);

    private readonly ConcurrentDictionary<string, CachedResponse> _etagCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DateTimeOffset> _hourlyRestTimestamps = new();
    private readonly object _lock = new();

    private int? _coreLimit;
    private int? _coreRemaining;
    private int? _coreUsed;
    private DateTimeOffset? _coreResetAt;
    private int? _graphQlLimit;
    private int? _graphQlRemaining;
    private int? _graphQlUsed;
    private DateTimeOffset? _graphQlResetAt;

    private long _restCallsTotal;
    private long _cache304HitsTotal;
    private long _graphQlCallsTotal;
    private long _callsSkippedByQuota;

    public bool IsRestPaused(out string reason)
    {
        var status = GetStatus();
        if (status.Mode == "paused")
        {
            reason = status.CoreRemaining is < PauseThreshold
                ? $"GitHub core remaining={status.CoreRemaining} (<{PauseThreshold}), paused until {status.CoreResetAt:HH:mm:ss}Z"
                : $"Hourly twin REST budget reached ({status.TwinRestCallsThisHour}/{status.BudgetPerHourTarget})";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    public bool IsRestBackoff() => GetStatus().Mode == "backoff";

    public bool IsGraphQlPaused(out string reason)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_graphQlRemaining is < PauseThreshold && (_graphQlResetAt is null || now < _graphQlResetAt.Value))
            {
                reason = $"GitHub GraphQL remaining={_graphQlRemaining} (<{PauseThreshold}), paused until {_graphQlResetAt:HH:mm:ss}Z";
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }

    public GitHubRateLimitStatus GetStatus()
    {
        var now = timeProvider.GetUtcNow();
        PruneHourlyQueue(now);
        var hourlyCalls = _hourlyRestTimestamps.Count;

        lock (_lock)
        {
            if (_coreResetAt is not null && now >= _coreResetAt.Value && _coreRemaining is < PauseThreshold)
            {
                _coreRemaining = _coreLimit ?? 5000;
            }

            string mode;
            if ((_coreRemaining is < PauseThreshold && (_coreResetAt is null || now < _coreResetAt.Value)) ||
                hourlyCalls >= HourlyRestBudgetTarget - 100)
            {
                mode = "paused";
            }
            else if (_coreRemaining is < BackoffThreshold || hourlyCalls >= 900)
            {
                mode = "backoff";
            }
            else if (_coreRemaining is not null)
            {
                mode = "normal";
            }
            else
            {
                mode = "unknown";
            }

            return new GitHubRateLimitStatus(
                _coreLimit,
                _coreRemaining,
                _coreUsed,
                _coreResetAt,
                _graphQlLimit,
                _graphQlRemaining,
                _graphQlResetAt,
                mode,
                hourlyCalls,
                Interlocked.Read(ref _restCallsTotal),
                Interlocked.Read(ref _cache304HitsTotal),
                Interlocked.Read(ref _graphQlCallsTotal),
                Interlocked.Read(ref _callsSkippedByQuota),
                HourlyRestBudgetTarget);
        }
    }

    public async Task<GitHubFetchResult> GetApiAsync(string relativePath, bool bypassPause, CancellationToken ct)
    {
        if (!bypassPause && IsRestPaused(out _))
        {
            Interlocked.Increment(ref _callsSkippedByQuota);
            _etagCache.TryGetValue($"api:{relativePath}", out var stale);
            return new GitHubFetchResult(false, false, true, HttpStatusCode.TooManyRequests, stale?.Body);
        }

        using var client = CreateApiClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, relativePath);
        var cacheKey = $"api:{relativePath}";

        if (_etagCache.TryGetValue(cacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached.ETag))
        {
            req.Headers.IfNoneMatch.TryParseAdd(cached.ETag);
        }

        using var resp = await client.SendAsync(req, ct);
        UpdateRateLimitFromHeaders(resp.Headers);

        if (resp.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            Interlocked.Increment(ref _cache304HitsTotal);
            return new GitHubFetchResult(true, true, false, resp.StatusCode, cached.Body);
        }

        Interlocked.Increment(ref _restCallsTotal);
        RecordHourlyCall(timeProvider.GetUtcNow());

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            MarkRateLimitIfExhausted(resp);
            return new GitHubFetchResult(false, false, true, resp.StatusCode, cached?.Body);
        }

        if (!resp.IsSuccessStatusCode)
        {
            return new GitHubFetchResult(false, false, false, resp.StatusCode, null);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var etag = resp.Headers.ETag?.ToString();
        _etagCache[cacheKey] = new CachedResponse(etag, body, timeProvider.GetUtcNow());
        return new GitHubFetchResult(true, false, false, resp.StatusCode, body);
    }

    /// <summary>
    /// Reads public raw files from raw.githubusercontent.com with ETag caching.
    /// Does NOT consume api.github.com 5,000/hr REST rate limit.
    /// </summary>
    public async Task<string?> GetRawPublicAsync(string org, string repo, string branch, string filePath, CancellationToken ct)
    {
        var url = $"https://raw.githubusercontent.com/{org}/{repo}/{Uri.EscapeDataString(branch)}/{filePath}";
        var cacheKey = $"raw:{url}";

        using var client = httpClientFactory.CreateClient("github-raw");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("cmdemo1-digitaltwin/1.0");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        if (_etagCache.TryGetValue(cacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached.ETag))
        {
            req.Headers.IfNoneMatch.TryParseAdd(cached.ETag);
        }

        using var resp = await client.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Body;
        }

        if (!resp.IsSuccessStatusCode)
        {
            return cached?.Body;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var etag = resp.Headers.ETag?.ToString();
        _etagCache[cacheKey] = new CachedResponse(etag, body, timeProvider.GetUtcNow());
        return body;
    }

    public async Task<GitHubFetchResult> PostGraphQlAsync(string query, object? variables, CancellationToken ct)
    {
        if (IsGraphQlPaused(out _))
        {
            Interlocked.Increment(ref _callsSkippedByQuota);
            return new GitHubFetchResult(false, false, true, HttpStatusCode.TooManyRequests, null);
        }

        using var client = CreateApiClient();
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync("graphql", content, ct);
        UpdateRateLimitFromHeaders(resp.Headers, defaultResource: "graphql");
        Interlocked.Increment(ref _graphQlCallsTotal);

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            MarkRateLimitIfExhausted(resp, isGraphQl: true);
            return new GitHubFetchResult(false, false, true, resp.StatusCode, null);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Contains("RATE_LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                _graphQlRemaining = 0;
                _graphQlResetAt ??= timeProvider.GetUtcNow().AddMinutes(15);
            }
            return new GitHubFetchResult(false, false, true, HttpStatusCode.TooManyRequests, body);
        }

        return new GitHubFetchResult(resp.IsSuccessStatusCode, false, false, resp.StatusCode, body);
    }

    public async Task<HttpResponseMessage> SendMutationAsync(HttpMethod method, string relativePath, object payload, CancellationToken ct)
    {
        using var client = CreateApiClient();
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(method, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var resp = await client.SendAsync(req, ct);
        UpdateRateLimitFromHeaders(resp.Headers);
        Interlocked.Increment(ref _restCallsTotal);
        RecordHourlyCall(timeProvider.GetUtcNow());
        return resp;
    }

    public void RecordRateLimitSnapshotForTest(HttpResponseHeaders headers, string defaultResource = "core")
        => UpdateRateLimitFromHeaders(headers, defaultResource);

    private HttpClient CreateApiClient()
    {
        var client = httpClientFactory.CreateClient("github");
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("cmdemo1-digitaltwin/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(options.Value.GitHubToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.GitHubToken);
        }
        return client;
    }

    private void UpdateRateLimitFromHeaders(HttpResponseHeaders headers, string defaultResource = "core")
    {
        var resource = TryGetHeader(headers, "X-RateLimit-Resource") ?? defaultResource;
        var limit = TryGetIntHeader(headers, "X-RateLimit-Limit");
        var remaining = TryGetIntHeader(headers, "X-RateLimit-Remaining");
        var used = TryGetIntHeader(headers, "X-RateLimit-Used");
        var resetEpoch = TryGetLongHeader(headers, "X-RateLimit-Reset");
        DateTimeOffset? resetAt = resetEpoch is > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetEpoch.Value) : null;

        if (limit is null && remaining is null && resetAt is null)
        {
            return;
        }

        lock (_lock)
        {
            if (string.Equals(resource, "graphql", StringComparison.OrdinalIgnoreCase))
            {
                if (limit is not null) _graphQlLimit = limit;
                if (remaining is not null) _graphQlRemaining = remaining;
                if (used is not null) _graphQlUsed = used;
                if (resetAt is not null) _graphQlResetAt = resetAt;
            }
            else if (string.Equals(resource, "core", StringComparison.OrdinalIgnoreCase))
            {
                if (limit is not null) _coreLimit = limit;
                if (remaining is not null) _coreRemaining = remaining;
                if (used is not null) _coreUsed = used;
                if (resetAt is not null) _coreResetAt = resetAt;
            }
        }
    }

    private void MarkRateLimitIfExhausted(HttpResponseMessage resp, bool isGraphQl = false)
    {
        var remaining = TryGetIntHeader(resp.Headers, "X-RateLimit-Remaining");
        if (remaining is not 0 && resp.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return;
        }

        lock (_lock)
        {
            if (isGraphQl)
            {
                _graphQlRemaining = 0;
                _graphQlResetAt ??= timeProvider.GetUtcNow().AddMinutes(15);
            }
            else
            {
                _coreRemaining = 0;
                _coreResetAt ??= timeProvider.GetUtcNow().AddMinutes(15);
            }
        }
    }

    private void RecordHourlyCall(DateTimeOffset now)
    {
        _hourlyRestTimestamps.Enqueue(now);
        PruneHourlyQueue(now);
    }

    private void PruneHourlyQueue(DateTimeOffset now)
    {
        var cutoff = now.AddHours(-1);
        while (_hourlyRestTimestamps.TryPeek(out var oldest) && oldest < cutoff)
        {
            _hourlyRestTimestamps.TryDequeue(out _);
        }
    }

    private static string? TryGetHeader(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static int? TryGetIntHeader(HttpResponseHeaders headers, string name)
        => int.TryParse(TryGetHeader(headers, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? TryGetLongHeader(HttpResponseHeaders headers, string name)
        => long.TryParse(TryGetHeader(headers, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
