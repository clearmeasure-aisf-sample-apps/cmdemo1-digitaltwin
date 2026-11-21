using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DigitalTwin.Application.Abstractions;
using DigitalTwin.Application.Services;
using DigitalTwin.Domain.BuildAndArtifact;
using DigitalTwin.Domain.Hierarchy;
using DigitalTwin.Domain.Observation;
using DigitalTwin.Domain.ReliabilityAndDrift;
using DigitalTwin.Domain.RuntimeAndPlatform;
using DigitalTwin.Domain.SourceAndChange;
using DigitalTwin.Domain.Work;
using DigitalTwin.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Infrastructure.Clients;

public sealed partial class GitHubSyncService(
    IHttpClientFactory httpClientFactory,
    ITwinStore store,
    EventIngestionService ingestion,
    IOptions<TwinOptions> options,
    TimeProvider timeProvider,
    ILogger<GitHubSyncService> logger) : IGitHubSyncService
{
    private static readonly string[] ActiveStages = ["tdd", "uat", "prod"];
    private static readonly string[] AllWebhookEvents = ["*"];
    private DateTimeOffset _lastBoardGraphQlSync = DateTimeOffset.MinValue;

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub sync failed")]
    private partial void LogGitHubSyncFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to register GitHub webhook on {Repo}")]
    private partial void LogWebhookRegistrationFailed(Exception ex, string repo);

    public async Task SyncAsync(bool fullBackfill, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = new IngestionCursor
        {
            SourceKey = "github",
            LastAttemptAt = now,
            Status = "running"
        };

        // Check GitHub rate limit before burning shared token quota
        if (!fullBackfill)
        {
            using var rateClient = CreateClient();
            var rateJson = await GetStringOrNullAsync(rateClient, "rate_limit", ct);
            if (!string.IsNullOrWhiteSpace(rateJson))
            {
                using var rateDoc = JsonDocument.Parse(rateJson);
                if (rateDoc.RootElement.TryGetProperty("resources", out var res) &&
                    res.TryGetProperty("core", out var core) &&
                    core.TryGetProperty("remaining", out var rem) &&
                    rem.TryGetInt32(out var remaining) &&
                    remaining < 500)
                {
                    cursor.Status = "rate-limited-backoff";
                    cursor.LastError = $"GitHub core rate limit low ({remaining} remaining); relying on webhooks";
                    await store.UpsertCursorAsync(cursor, ct);
                    return;
                }
            }
        }

        try
        {
            using var client = CreateClient();

            // 1. Sync GitOps desired state from cmdemo1-system
            await SyncGitOpsDesiredStateAsync(client, now, ct);

            // 2. Sync repositories, commits, PRs, workflow runs, releases, and webhook statuses (4 concurrent)
            var repos = await DiscoverCmdemo1RepositoriesAsync(client, ct);
            var repoBag = new System.Collections.Concurrent.ConcurrentBag<RepositorySnapshot>();
            var prBag = new System.Collections.Concurrent.ConcurrentBag<SourceChangeSnapshot>();
            var runBag = new System.Collections.Concurrent.ConcurrentBag<PipelineRunSnapshot>();
            var artifactBag = new System.Collections.Concurrent.ConcurrentBag<ArtifactPackageSnapshot>();
            var eventBag = new System.Collections.Concurrent.ConcurrentBag<ObservedEvent>();
            var hookBag = new System.Collections.Concurrent.ConcurrentBag<WebhookRegistrationStatus>();

            using var repoGate = new SemaphoreSlim(4, 4);
            await Task.WhenAll(repos.Select(async repoName =>
            {
                await repoGate.WaitAsync(ct);
                try
                {
                    var rList = new List<RepositorySnapshot>();
                    var pList = new List<SourceChangeSnapshot>();
                    var rnList = new List<PipelineRunSnapshot>();
                    var aList = new List<ArtifactPackageSnapshot>();
                    var eList = new List<ObservedEvent>();
                    var hList = new List<WebhookRegistrationStatus>();

                    await SyncRepositoryAsync(client, repoName, fullBackfill, now, rList, pList, rnList, aList, eList, hList, ct);

                    foreach (var x in rList) repoBag.Add(x);
                    foreach (var x in pList) prBag.Add(x);
                    foreach (var x in rnList) runBag.Add(x);
                    foreach (var x in aList) artifactBag.Add(x);
                    foreach (var x in eList) eventBag.Add(x);
                    foreach (var x in hList) hookBag.Add(x);
                }
                finally
                {
                    repoGate.Release();
                }
            }));

            var repoSnapshots = repoBag.ToList();
            var prSnapshots = prBag.ToList();
            var runSnapshots = runBag.ToList();
            var artifactSnapshots = artifactBag.ToList();
            var newEvents = eventBag.ToList();
            var webhookStatuses = hookBag.ToList();

            await store.UpsertRepositoriesAsync(repoSnapshots, ct);
            await store.UpsertSourceChangesAsync(prSnapshots, ct);
            await store.UpsertPipelineRunsAsync(runSnapshots, ct);
            await store.UpsertArtifactsAsync(artifactSnapshots, ct);
            await store.ReplaceWebhookStatusesAsync("github", webhookStatuses, ct);

            // 3. Sync Work Items from status/workitems.json + throttled live Project 679 GraphQL (at most every 15 min)
            var queryBoardGraphQl = fullBackfill || (now - _lastBoardGraphQlSync) >= TimeSpan.FromMinutes(15);
            await SyncWorkItemsAsync(client, queryBoardGraphQl, now, ct);
            if (queryBoardGraphQl)
            {
                _lastBoardGraphQlSync = now;
            }

            var ingestedCount = await ingestion.IngestBatchAsync(newEvents, ct);

            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.Status = "ok";
            cursor.ItemsProcessed = repoSnapshots.Count + prSnapshots.Count + runSnapshots.Count + ingestedCount;
            cursor.LastCursor = now.ToString("O", CultureInfo.InvariantCulture);
            await store.UpsertCursorAsync(cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGitHubSyncFailed(ex);
            cursor.Status = "error";
            cursor.LastError = ex.Message;
            await store.UpsertCursorAsync(cursor, ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<WebhookRegistrationStatus>> EnsureWebhooksRegisteredAsync(string twinPublicBaseUrl, CancellationToken ct)
    {
        var baseUrl = twinPublicBaseUrl.TrimEnd('/');
        var targetUrl = $"{baseUrl}/hooks/github";
        var secret = options.Value.WebhookSecret;
        var results = new List<WebhookRegistrationStatus>();

        if (string.IsNullOrWhiteSpace(options.Value.GitHubToken) || string.IsNullOrWhiteSpace(secret))
        {
            return results;
        }

        using var client = CreateClient();
        var repos = await DiscoverCmdemo1RepositoriesAsync(client, ct);
        var now = timeProvider.GetUtcNow();

        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var repoMetaJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repo}", ct);
                if (!string.IsNullOrWhiteSpace(repoMetaJson))
                {
                    using var metaDoc = JsonDocument.Parse(repoMetaJson);
                    if (metaDoc.RootElement.TryGetProperty("archived", out var arch) && arch.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }
                }

                var hooksJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repo}/hooks", ct);
                long? existingId = null;
                if (!string.IsNullOrWhiteSpace(hooksJson))
                {
                    using var doc = JsonDocument.Parse(hooksJson);
                    foreach (var hook in doc.RootElement.EnumerateArray())
                    {
                        var url = hook.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("url", out var u)
                            ? u.GetString()
                            : null;
                        if (!string.IsNullOrWhiteSpace(url) &&
                            (url.Equals(targetUrl, StringComparison.OrdinalIgnoreCase) ||
                             url.EndsWith("/hooks/github", StringComparison.OrdinalIgnoreCase) && url.Contains("cmdemo1-twin", StringComparison.OrdinalIgnoreCase)))
                        {
                            existingId = hook.GetProperty("id").GetInt64();
                            break;
                        }
                    }
                }

                var payload = JsonSerializer.Serialize(new
                {
                    name = "web",
                    active = true,
                    events = AllWebhookEvents,
                    config = new
                    {
                        url = targetUrl,
                        content_type = "json",
                        secret,
                        insecure_ssl = "0"
                    }
                });

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                HttpResponseMessage response = existingId is null
                    ? await client.PostAsync($"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repo}/hooks", content, ct)
                    : await client.PatchAsync($"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repo}/hooks/{existingId.Value}", content, ct);

                var respBody = await response.Content.ReadAsStringAsync(ct);
                long? hookId = existingId;
                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(respBody))
                {
                    using var hookDoc = JsonDocument.Parse(respBody);
                    if (hookDoc.RootElement.TryGetProperty("id", out var idEl))
                    {
                        hookId = idEl.GetInt64();
                    }
                }

                results.Add(new WebhookRegistrationStatus
                {
                    Id = $"github:{repo}",
                    Source = "github",
                    Target = repo,
                    EndpointUrl = targetUrl,
                    IsActive = response.IsSuccessStatusCode,
                    ExternalId = hookId?.ToString(CultureInfo.InvariantCulture),
                    Detail = response.IsSuccessStatusCode ? "registered" : $"HTTP {(int)response.StatusCode}: {respBody}",
                    CheckedAt = now
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWebhookRegistrationFailed(ex, repo);
                results.Add(new WebhookRegistrationStatus
                {
                    Id = $"github:{repo}",
                    Source = "github",
                    Target = repo,
                    EndpointUrl = targetUrl,
                    IsActive = false,
                    Detail = ex.Message,
                    CheckedAt = now
                });
            }
        }

        await store.ReplaceWebhookStatusesAsync("github", results, ct);
        return results;
    }

    private async Task SyncGitOpsDesiredStateAsync(HttpClient client, DateTimeOffset now, CancellationToken ct)
    {
        var systemJson = await FetchRawFileAsync(client, Cmdemo1PlatformCatalog.SystemRepository, "main", "system.json", ct) ?? "{}";
        var pinned = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var stage in ActiveStages)
        {
            var vJson = await FetchRawFileAsync(client, Cmdemo1PlatformCatalog.SystemRepository, "main", $"environments/{stage}/versions.json", ct);
            if (!string.IsNullOrWhiteSpace(vJson))
            {
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(vJson);
                    if (map is not null)
                    {
                        pinned[stage] = map;
                    }
                }
                catch (JsonException)
                {
                    // ignore malformed partial read
                }
            }
        }

        var costJson = await FetchRawFileAsync(client, Cmdemo1PlatformCatalog.SystemRepository, "status", "cost.json", ct);
        var containersJson = await FetchRawFileAsync(client, Cmdemo1PlatformCatalog.SystemRepository, "status", "containers.json", ct);

        await store.UpsertDesiredConfigAsync(new DesiredSystemConfigSnapshot
        {
            Id = Cmdemo1PlatformCatalog.SystemSlug,
            SystemName = Cmdemo1PlatformCatalog.SystemName,
            SystemJson = systemJson,
            PinnedVersionsJson = JsonSerializer.Serialize(pinned),
            NodesJson = containersJson,
            CostJson = costJson,
            UpdatedAt = now
        }, ct);

        // Seed EnvironmentInstanceSnapshots from cost.json if Azure hasn't populated yet
        if (!string.IsNullOrWhiteSpace(costJson))
        {
            var existingStages = (await store.GetEnvironmentInstancesAsync(ct))
                .ToDictionary(s => s.StageName, StringComparer.OrdinalIgnoreCase);

            using var costDoc = JsonDocument.Parse(costJson);
            if (costDoc.RootElement.TryGetProperty("environments", out var envCosts) && envCosts.ValueKind == JsonValueKind.Array)
            {
                var updated = new List<EnvironmentInstanceSnapshot>();
                foreach (var stageDef in Cmdemo1PlatformCatalog.Stages)
                {
                    existingStages.TryGetValue(stageDef.Name, out var inst);
                    inst ??= new EnvironmentInstanceSnapshot
                    {
                        StageName = stageDef.Name,
                        Tier = stageDef.Tier,
                        HostingBoundary = stageDef.HostingBoundary,
                        ResourceGroup = stageDef.ResourceGroup,
                        IsDeclaredOnly = stageDef.IsDeclaredOnly,
                        IsHealthy = !stageDef.IsDeclaredOnly,
                        LastEvaluatedAt = now
                    };

                    var costEntry = envCosts.EnumerateArray().FirstOrDefault(e =>
                        e.TryGetProperty("name", out var n) &&
                        string.Equals(n.GetString(), stageDef.Name, StringComparison.OrdinalIgnoreCase));

                    if (costEntry.ValueKind == JsonValueKind.Object)
                    {
                        inst.CostMonthToDateUsd = costEntry.TryGetProperty("monthToDate", out var mtd) && mtd.TryGetDouble(out var m) ? m : inst.CostMonthToDateUsd;
                        inst.CostYesterdayUsd = costEntry.TryGetProperty("yesterday", out var y) && y.TryGetDouble(out var yv) ? yv : inst.CostYesterdayUsd;
                    }
                    updated.Add(inst);
                }
                await store.UpsertEnvironmentInstancesAsync(updated, ct);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> DiscoverCmdemo1RepositoriesAsync(HttpClient client, CancellationToken ct)
    {
        var names = new HashSet<string>(Cmdemo1PlatformCatalog.ObservedRepositories, StringComparer.OrdinalIgnoreCase);
        var json = await GetStringOrNullAsync(client, $"search/repositories?q=org:{Cmdemo1PlatformCatalog.GitHubOrg}+cmdemo1-+in:name&per_page=100", ct);
        if (!string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var repo in items.EnumerateArray())
                {
                    var name = repo.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("cmdemo1-", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task SyncRepositoryAsync(
        HttpClient client,
        string repoName,
        bool fullBackfill,
        DateTimeOffset now,
        List<RepositorySnapshot> repoSnapshots,
        List<SourceChangeSnapshot> prSnapshots,
        List<PipelineRunSnapshot> runSnapshots,
        List<ArtifactPackageSnapshot> artifactSnapshots,
        List<ObservedEvent> newEvents,
        List<WebhookRegistrationStatus> webhookStatuses,
        CancellationToken ct)
    {
        var repoJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}", ct);
        if (string.IsNullOrWhiteSpace(repoJson))
        {
            return;
        }

        using var repoDoc = JsonDocument.Parse(repoJson);
        var root = repoDoc.RootElement;
        var defaultBranch = root.TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main";
        var htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? $"https://github.com/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}" : "";
        var isPrivate = root.TryGetProperty("private", out var priv) && priv.ValueKind == JsonValueKind.True;
        var pipelineId = Cmdemo1PlatformCatalog.ResolvePipelineIdFromRepository(repoName);
        var deployable = Cmdemo1PlatformCatalog.ResolveDeployableFromRepository(repoName);
        var isTwin = repoName.Equals(Cmdemo1PlatformCatalog.TwinRepository, StringComparison.OrdinalIgnoreCase);
        var isArchived = root.TryGetProperty("archived", out var arch) && arch.ValueKind == JsonValueKind.True;
        var isRetired = isArchived || repoName is "cmdemo1-aisf" or "cmdemo1-fworker" or "cmdemo1-app2-ui" or "cmdemo1-scorecard";

        // Recent commits
        var commitsCount = fullBackfill ? 10 : 4;
        var commitsJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}/commits?sha={Uri.EscapeDataString(defaultBranch)}&per_page={commitsCount}", ct);
        string? latestSha = null;
        string? latestMsg = null;
        string? latestAuthor = null;
        DateTimeOffset? latestCommitAt = null;

        if (!string.IsNullOrWhiteSpace(commitsJson))
        {
            using var commitsDoc = JsonDocument.Parse(commitsJson);
            if (commitsDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in commitsDoc.RootElement.EnumerateArray())
                {
                    var sha = c.TryGetProperty("sha", out var s) ? s.GetString() ?? "" : "";
                    var commitObj = c.GetProperty("commit");
                    var msg = (commitObj.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "").Split('\n')[0];
                    var authorName = commitObj.TryGetProperty("author", out var a) && a.TryGetProperty("name", out var an) ? an.GetString() ?? "unknown" : "unknown";
                    var dateStr = commitObj.TryGetProperty("committer", out var cm) && cm.TryGetProperty("date", out var d) ? d.GetString() : null;
                    var commitAt = DateTimeOffset.TryParse(dateStr, out var dt) ? dt.ToUniversalTime() : now;
                    var commitUrl = c.TryGetProperty("html_url", out var cu) ? cu.GetString() : htmlUrl;

                    if (latestSha is null)
                    {
                        latestSha = sha;
                        latestMsg = msg;
                        latestAuthor = authorName;
                        latestCommitAt = commitAt;
                    }

                    var shortSha = sha.Length >= 7 ? sha[..7] : sha;
                    newEvents.Add(new ObservedEvent
                    {
                        EventId = $"commit-{repoName}-{shortSha}",
                        IdempotencyKey = $"gh-commit:{repoName}:{sha}",
                        Source = EventSourceKind.GitHub,
                        SourceEventId = sha,
                        EventType = "dev.cdevents.repository.modified",
                        SubjectType = "commit",
                        SubjectId = $"{repoName}@{shortSha}",
                        Predicate = "committed",
                        OccurredAt = commitAt,
                        IngestedAt = now,
                        Channel = fullBackfill ? IngestionChannel.Backfill : IngestionChannel.Poll,
                        CorrelationId = $"sha:{sha}",
                        ChainId = $"sha:{sha}",
                        PipelineId = pipelineId,
                        DeployableName = deployable,
                        Actor = authorName,
                        Outcome = EventOutcome.Success,
                        Summary = $"[{repoName}/{defaultBranch}] {shortSha}: {Truncate(msg, 85)}",
                        SourceUrl = commitUrl,
                        PayloadJson = c.GetRawText()
                    });
                }
            }
        }

        // PRs
        var prCount = fullBackfill ? 25 : 12;
        var prsJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}/pulls?state=all&sort=updated&direction=desc&per_page={prCount}", ct);
        var openPrs = 0;
        if (!string.IsNullOrWhiteSpace(prsJson))
        {
            using var prsDoc = JsonDocument.Parse(prsJson);
            if (prsDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var pr in prsDoc.RootElement.EnumerateArray())
                {
                    var num = pr.GetProperty("number").GetInt32();
                    var state = pr.TryGetProperty("state", out var st) ? st.GetString() ?? "open" : "open";
                    var mergedAtStr = pr.TryGetProperty("merged_at", out var ma) && ma.ValueKind == JsonValueKind.String ? ma.GetString() : null;
                    var mergedAt = DateTimeOffset.TryParse(mergedAtStr, out var mdt) ? mdt.ToUniversalTime() : (DateTimeOffset?)null;
                    if (mergedAt is not null) state = "merged";
                    if (state == "open") openPrs++;

                    var title = pr.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var author = pr.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var ul) ? ul.GetString() ?? "unknown" : "unknown";
                    var branch = pr.TryGetProperty("head", out var h) && h.TryGetProperty("ref", out var hr) ? hr.GetString() ?? "" : "";
                    var mergeSha = pr.TryGetProperty("merge_commit_sha", out var ms) && ms.ValueKind == JsonValueKind.String ? ms.GetString() : null;
                    var createdAt = pr.TryGetProperty("created_at", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    var updatedAt = pr.TryGetProperty("updated_at", out var ua) && DateTimeOffset.TryParse(ua.GetString(), out var udt) ? udt.ToUniversalTime() : now;

                    prSnapshots.Add(new SourceChangeSnapshot
                    {
                        Id = $"{repoName}#{num}",
                        Repository = repoName,
                        Number = num,
                        Title = title,
                        Url = pr.TryGetProperty("html_url", out var pu) ? pu.GetString() ?? "" : "",
                        State = state,
                        HeadBranch = branch,
                        BaseBranch = pr.TryGetProperty("base", out var b) && b.TryGetProperty("ref", out var br) ? br.GetString() ?? defaultBranch : defaultBranch,
                        Author = author,
                        IsBot = author.Contains("bot", StringComparison.OrdinalIgnoreCase) || author.Contains("cursor", StringComparison.OrdinalIgnoreCase) || author.Contains("copilot", StringComparison.OrdinalIgnoreCase),
                        PipelineId = pipelineId,
                        LinkedIssueNumber = ExtractIssueNumber(title, branch),
                        MergeCommitSha = mergeSha,
                        CreatedAt = createdAt,
                        UpdatedAt = updatedAt,
                        MergedAt = mergedAt
                    });
                }
            }
        }

        // Workflow runs
        var runsCount = fullBackfill ? 20 : 10;
        var runsJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}/actions/runs?per_page={runsCount}", ct);
        if (!string.IsNullOrWhiteSpace(runsJson))
        {
            using var runsDoc = JsonDocument.Parse(runsJson);
            if (runsDoc.RootElement.TryGetProperty("workflow_runs", out var runsArr) && runsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in runsArr.EnumerateArray())
                {
                    var runId = r.GetProperty("id").GetInt64();
                    var runNumber = r.TryGetProperty("run_number", out var rn) ? rn.GetInt64() : runId;
                    var workflowName = r.TryGetProperty("name", out var wn) ? wn.GetString() ?? "Workflow" : "Workflow";
                    var displayTitle = r.TryGetProperty("display_title", out var dt) ? dt.GetString() ?? workflowName : workflowName;
                    var eventName = r.TryGetProperty("event", out var ev) ? ev.GetString() ?? "push" : "push";
                    var branch = r.TryGetProperty("head_branch", out var hb) ? hb.GetString() ?? "" : "";
                    var headSha = r.TryGetProperty("head_sha", out var hs) ? hs.GetString() ?? "" : "";
                    var status = r.TryGetProperty("status", out var st) ? st.GetString() ?? "completed" : "completed";
                    var conclusion = r.TryGetProperty("conclusion", out var co) && co.ValueKind == JsonValueKind.String ? co.GetString() : null;
                    var runUrl = r.TryGetProperty("html_url", out var ru) ? ru.GetString() ?? "" : "";
                    var createdAt = r.TryGetProperty("created_at", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    var updatedAt = r.TryGetProperty("updated_at", out var ua) && DateTimeOffset.TryParse(ua.GetString(), out var udt) ? udt.ToUniversalTime() : now;
                    var version = ExtractLeadingVersion(displayTitle);

                    runSnapshots.Add(new PipelineRunSnapshot
                    {
                        Id = $"{repoName}:{runId}",
                        RunId = runId,
                        RunNumber = runNumber,
                        Repository = repoName,
                        PipelineId = pipelineId,
                        WorkflowName = workflowName,
                        DisplayTitle = displayTitle,
                        Version = version,
                        EventName = eventName,
                        Branch = branch,
                        HeadSha = headSha,
                        Status = status,
                        Conclusion = conclusion,
                        Url = runUrl,
                        CreatedAt = createdAt,
                        UpdatedAt = updatedAt
                    });

                    if (status == "completed")
                    {
                        newEvents.Add(new ObservedEvent
                        {
                            EventId = $"ghrun-{runId}",
                            IdempotencyKey = $"gh-run:{repoName}:{runId}:{status}",
                            Source = EventSourceKind.GitHub,
                            SourceEventId = runId.ToString(CultureInfo.InvariantCulture),
                            EventType = "dev.cdevents.pipelineRun.finished",
                            SubjectType = "pipelineRun",
                            SubjectId = $"{repoName}:run:{runId}",
                            Predicate = "finished",
                            OccurredAt = updatedAt,
                            IngestedAt = now,
                            Channel = fullBackfill ? IngestionChannel.Backfill : IngestionChannel.Poll,
                            CorrelationId = string.IsNullOrEmpty(headSha) ? null : $"sha:{headSha}",
                            ChainId = string.IsNullOrEmpty(headSha) ? null : $"sha:{headSha}",
                            PipelineId = pipelineId,
                            DeployableName = deployable,
                            Actor = "github-actions",
                            Outcome = conclusion switch
                            {
                                "success" => EventOutcome.Success,
                                "failure" or "timed_out" => EventOutcome.Failure,
                                "cancelled" => EventOutcome.Canceled,
                                "skipped" => EventOutcome.Skipped,
                                _ => EventOutcome.None
                            },
                            Summary = $"[{repoName}/{branch}] {workflowName} #{runNumber} {conclusion ?? status}",
                            SourceUrl = runUrl,
                            PayloadJson = r.GetRawText()
                        });
                    }
                }
            }
        }

        // GitHub release deployments / packages
        var deploymentsJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}/deployments?per_page=10", ct);
        if (!string.IsNullOrWhiteSpace(deploymentsJson))
        {
            using var depDoc = JsonDocument.Parse(deploymentsJson);
            if (depDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in depDoc.RootElement.EnumerateArray())
                {
                    var env = dep.TryGetProperty("environment", out var e) ? e.GetString() ?? "release" : "release";
                    var sha = dep.TryGetProperty("sha", out var s) ? s.GetString() ?? "" : "";
                    var refName = dep.TryGetProperty("ref", out var rf) ? rf.GetString() ?? "" : "";
                    var depId = dep.GetProperty("id").GetInt64();
                    var createdAt = dep.TryGetProperty("created_at", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    artifactSnapshots.Add(new ArtifactPackageSnapshot
                    {
                        Id = $"GitHubDeployment:{repoName}:{depId}",
                        Kind = "GitHubReleaseDeployment",
                        Name = $"{repoName}/{env}",
                        Version = string.IsNullOrEmpty(refName) ? (sha.Length >= 7 ? sha[..7] : depId.ToString(CultureInfo.InvariantCulture)) : refName,
                        RegistryOrFeed = "github-deployments",
                        CommitSha = sha,
                        PipelineId = pipelineId,
                        Url = $"{htmlUrl}/deployments",
                        PublishedAt = createdAt
                    });
                }
            }
        }

        // Webhooks & recent delivery check
        var hooksJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}/hooks", ct);
        var webhookRegistered = false;
        long? webhookId = null;
        int? lastDeliveryStatus = null;
        DateTimeOffset? lastDeliveryAt = null;
        string? hookUrl = null;

        if (!string.IsNullOrWhiteSpace(hooksJson))
        {
            using var hooksDoc = JsonDocument.Parse(hooksJson);
            if (hooksDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var hook in hooksDoc.RootElement.EnumerateArray())
                {
                    var active = hook.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True;
                    var url = hook.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url) && url.EndsWith("/hooks/github", StringComparison.OrdinalIgnoreCase))
                    {
                        webhookRegistered = active;
                        webhookId = hook.GetProperty("id").GetInt64();
                        hookUrl = url;

                        if (fullBackfill)
                        {
                            var deliveriesJson = await GetStringOrNullAsync(client, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}/hooks/{webhookId.Value}/deliveries?per_page=2", ct);
                            if (!string.IsNullOrWhiteSpace(deliveriesJson))
                            {
                                using var delDoc = JsonDocument.Parse(deliveriesJson);
                                if (delDoc.RootElement.ValueKind == JsonValueKind.Array && delDoc.RootElement.GetArrayLength() > 0)
                                {
                                    var firstDel = delDoc.RootElement[0];
                                    lastDeliveryStatus = firstDel.TryGetProperty("status_code", out var sc) && sc.TryGetInt32(out var scv) ? scv : null;
                                    lastDeliveryAt = firstDel.TryGetProperty("delivered_at", out var da) && DateTimeOffset.TryParse(da.GetString(), out var ddt)
                                        ? ddt.ToUniversalTime()
                                        : null;
                                }
                            }
                        }
                        break;
                    }
                }
            }
        }

        webhookStatuses.Add(new WebhookRegistrationStatus
        {
            Id = $"github:{repoName}",
            Source = "github",
            Target = repoName,
            EndpointUrl = hookUrl ?? "/hooks/github",
            IsActive = webhookRegistered,
            ExternalId = webhookId?.ToString(CultureInfo.InvariantCulture),
            LastDeliveryStatusCode = lastDeliveryStatus,
            LastDeliveryAt = lastDeliveryAt,
            Detail = webhookRegistered ? "active" : "not registered",
            CheckedAt = now
        });

        var openIssues = root.TryGetProperty("open_issues_count", out var oic) && oic.TryGetInt32(out var oi) ? Math.Max(0, oi - openPrs) : 0;

        repoSnapshots.Add(new RepositorySnapshot
        {
            Name = repoName,
            FullName = $"{Cmdemo1PlatformCatalog.GitHubOrg}/{repoName}",
            Url = htmlUrl,
            DefaultBranch = defaultBranch,
            IsPrivate = isPrivate,
            IsRetired = isRetired,
            IsTwinObserver = isTwin,
            PipelineId = pipelineId,
            LatestCommitSha = latestSha,
            LatestCommitMessage = latestMsg,
            LatestCommitAuthor = latestAuthor,
            LatestCommitAt = latestCommitAt,
            OpenPullRequestCount = openPrs,
            OpenIssueCount = openIssues,
            WebhookRegistered = webhookRegistered,
            WebhookId = webhookId,
            LastWebhookStatusCode = lastDeliveryStatus,
            LastWebhookDeliveryAt = lastDeliveryAt,
            UpdatedAt = now
        });
    }

    private async Task SyncWorkItemsAsync(HttpClient client, bool queryBoardGraphQl, DateTimeOffset now, CancellationToken ct)
    {
        var itemsById = new Dictionary<string, WorkItemSnapshot>(StringComparer.OrdinalIgnoreCase);

        // First hydrate rich history & environment deployment correlation from cmdemo1-system status/workitems.json
        var workItemsJson = await FetchRawFileAsync(client, Cmdemo1PlatformCatalog.SystemRepository, "status", "workitems.json", ct);
        if (!string.IsNullOrWhiteSpace(workItemsJson))
        {
            using var doc = JsonDocument.Parse(workItemsJson);
            if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray().Take(150))
                {
                    var number = item.GetProperty("number").GetInt32();
                    var id = $"cmdemo1-workorders#{number}";
                    var createdAt = item.TryGetProperty("createdAt", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    var closedAt = item.TryGetProperty("closedAt", out var cla) && cla.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(cla.GetString(), out var cldt) ? cldt.ToUniversalTime() : (DateTimeOffset?)null;
                    var envs = item.TryGetProperty("environments", out var envEl) ? envEl : default;

                    itemsById[id] = new WorkItemSnapshot
                    {
                        Id = id,
                        Repository = "cmdemo1-workorders",
                        Number = number,
                        Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                        State = item.TryGetProperty("state", out var st) ? st.GetString() ?? "open" : "open",
                        Column = item.TryGetProperty("column", out var col) ? col.GetString() ?? "Todo" : "Todo",
                        ColumnSource = item.TryGetProperty("columnSource", out var cs) ? cs.GetString() ?? "derived" : "derived",
                        WorkerKind = item.TryGetProperty("worker", out var w) ? w.GetString() ?? "team" : "team",
                        Agent = item.TryGetProperty("agent", out var ag) && ag.ValueKind == JsonValueKind.String ? ag.GetString() : null,
                        Assignee = item.TryGetProperty("assignee", out var asg) && asg.ValueKind == JsonValueKind.String ? asg.GetString() : null,
                        CreatedAt = createdAt,
                        ClosedAt = closedAt,
                        ReleaseCandidateVersion = item.TryGetProperty("releaseCandidate", out var rc) && rc.ValueKind == JsonValueKind.Object && rc.TryGetProperty("version", out var rcv) ? rcv.GetString() : null,
                        TddVersion = GetNestedString(envs, "tdd", "version"),
                        TddDeployedAt = GetNestedDate(envs, "tdd", "deployedAt"),
                        UatVersion = GetNestedString(envs, "uat", "version"),
                        UatDeployedAt = GetNestedDate(envs, "uat", "deployedAt"),
                        ProdVersion = GetNestedString(envs, "prod", "version"),
                        ProdDeployedAt = GetNestedDate(envs, "prod", "deployedAt"),
                        FtFailures = item.TryGetProperty("ftFailures", out var ft) && ft.TryGetInt32(out var ftv) ? ftv : 0,
                        Reverts = item.TryGetProperty("reverts", out var rv) && rv.TryGetInt32(out var rvv) ? rvv : 0,
                        ColumnHistoryJson = item.TryGetProperty("columnHistory", out var ch) ? ch.GetRawText() : "[]",
                        LinkedPrsJson = item.TryGetProperty("prs", out var prsEl) ? prsEl.GetRawText() : "[]",
                        CursorSessionsJson = item.TryGetProperty("cursorSessions", out var curEl) ? curEl.GetRawText() : "[]",
                        LatestStatusUrl = item.TryGetProperty("latestStatusComment", out var lsc) && lsc.ValueKind == JsonValueKind.Object && lsc.TryGetProperty("url", out var lsu) ? lsu.GetString() : null,
                        LatestStatusKind = item.TryGetProperty("latestStatusComment", out var lsc2) && lsc2.ValueKind == JsonValueKind.Object && lsc2.TryGetProperty("kind", out var lsk) ? lsk.GetString() : null,
                        UpdatedAt = closedAt ?? createdAt
                    };
                }
            }
        }

        // Overlay authoritative live GitHub Projects v2 board (Project 679) columns via slim GraphQL (cost-efficient)
        if (!queryBoardGraphQl)
        {
            await store.UpsertWorkItemsAsync(itemsById.Values.ToList(), ct);
            return;
        }

        var graphqlQuery = """
        query($nodeId: ID!, $cursor: String) {
          node(id: $nodeId) {
            ... on ProjectV2 {
              items(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  fieldValueByName(name: "Status") {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  content {
                    ... on Issue {
                      number
                      title
                      url
                      state
                      createdAt
                      closedAt
                      updatedAt
                      repository { name }
                    }
                  }
                }
              }
            }
          }
        }
        """;

        string? cursor = null;
        for (var page = 0; page < 6; page++)
        {
            var body = JsonSerializer.Serialize(new
            {
                query = graphqlQuery,
                variables = new { nodeId = Cmdemo1PlatformCatalog.WorkItemsProjectNodeId, cursor }
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync("graphql", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                break;
            }
            var respJson = await resp.Content.ReadAsStringAsync(ct);
            using var gDoc = JsonDocument.Parse(respJson);
            if (!gDoc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("node", out var node) ||
                node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("items", out var itemsConn))
            {
                break;
            }

            if (itemsConn.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in nodes.EnumerateArray())
                {
                    if (!n.TryGetProperty("content", out var issue) || issue.ValueKind != JsonValueKind.Object ||
                        !issue.TryGetProperty("number", out var numEl))
                    {
                        continue;
                    }

                    var number = numEl.GetInt32();
                    var repo = issue.TryGetProperty("repository", out var rep) && rep.TryGetProperty("name", out var rn)
                        ? rn.GetString() ?? "cmdemo1-workorders"
                        : "cmdemo1-workorders";
                    var id = $"{repo}#{number}";
                    var statusCol = n.TryGetProperty("fieldValueByName", out var fv) && fv.ValueKind == JsonValueKind.Object && fv.TryGetProperty("name", out var fn)
                        ? fn.GetString()
                        : null;
                    var issueState = (issue.TryGetProperty("state", out var st) ? st.GetString() ?? "OPEN" : "OPEN").ToLowerInvariant();
                    var createdAt = issue.TryGetProperty("createdAt", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    var closedAt = issue.TryGetProperty("closedAt", out var cla) && cla.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(cla.GetString(), out var cldt) ? cldt.ToUniversalTime() : (DateTimeOffset?)null;
                    var updatedAt = issue.TryGetProperty("updatedAt", out var ua) && DateTimeOffset.TryParse(ua.GetString(), out var udt) ? udt.ToUniversalTime() : now;

                    if (itemsById.TryGetValue(id, out var existing))
                    {
                        if (!string.IsNullOrWhiteSpace(statusCol))
                        {
                            existing.Column = statusCol;
                            existing.ColumnSource = "project";
                        }
                        existing.State = issueState;
                        existing.ClosedAt = closedAt;
                        existing.UpdatedAt = updatedAt;
                    }
                    else
                    {
                        itemsById[id] = new WorkItemSnapshot
                        {
                            Id = id,
                            Repository = repo,
                            Number = number,
                            Title = issue.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                            Url = issue.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                            State = issueState,
                            Column = statusCol ?? (issueState == "closed" ? "Done" : "Todo"),
                            ColumnSource = string.IsNullOrWhiteSpace(statusCol) ? "derived" : "project",
                            WorkerKind = "team",
                            CreatedAt = createdAt,
                            ClosedAt = closedAt,
                            UpdatedAt = updatedAt
                        };
                    }

                    if (itemsById.TryGetValue(id, out var item))
                    {
                        if (item.ProdDeployedAt is not null)
                        {
                            item.LeadTimeHours = Math.Round((item.ProdDeployedAt.Value - item.CreatedAt).TotalHours, 1);
                        }
                        if (item.ClosedAt is not null)
                        {
                            item.CycleTimeHours ??= Math.Round((item.ClosedAt.Value - item.CreatedAt).TotalHours, 1);
                        }
                    }
                }
            }

            if (itemsConn.TryGetProperty("pageInfo", out var pi) &&
                pi.TryGetProperty("hasNextPage", out var hn) &&
                hn.ValueKind == JsonValueKind.True)
            {
                cursor = pi.TryGetProperty("endCursor", out var ec) ? ec.GetString() : null;
            }
            else
            {
                break;
            }
        }

        await store.UpsertWorkItemsAsync(itemsById.Values.ToList(), ct);
    }

    private HttpClient CreateClient()
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

    private static async Task<string?> GetStringOrNullAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var resp = await client.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static async Task<string?> FetchRawFileAsync(HttpClient client, string repo, string branch, string filePath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repo}/contents/{filePath}?ref={Uri.EscapeDataString(branch)}");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static int? ExtractIssueNumber(string title, string branch)
    {
        var idx = title.LastIndexOf("(#", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var end = title.IndexOf(')', idx + 2);
            if (end > idx + 2 && int.TryParse(title[(idx + 2)..end], out var n))
            {
                return n;
            }
        }
        foreach (var prefix in new[] { "factory-worker/", "issue-" })
        {
            if (branch.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = branch[prefix.Length..];
                var dash = rest.IndexOf('-');
                var numStr = dash >= 0 ? rest[..dash] : rest;
                if (int.TryParse(numStr, out var bn))
                {
                    return bn;
                }
            }
        }
        return null;
    }

    private static string? ExtractLeadingVersion(string displayTitle)
    {
        var firstToken = displayTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstToken is null) return null;
        var clean = firstToken.Split('-')[0];
        var parts = clean.Split('.');
        return parts.Length == 3 && parts.All(p => int.TryParse(p, out _)) ? firstToken : null;
    }

    private static string? GetNestedString(JsonElement el, string child, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(child, out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static DateTimeOffset? GetNestedDate(JsonElement el, string child, string prop)
        => DateTimeOffset.TryParse(GetNestedString(el, child, prop), out var dt) ? dt.ToUniversalTime() : null;

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : $"{text[..(max - 1)]}…";
}
