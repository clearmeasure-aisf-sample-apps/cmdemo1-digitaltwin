using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using DigitalTwin.Core.RuntimeAndPlatform;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.GitHub;

public sealed partial class GitHubSyncService(
    GitHubApiClient gh,
    ITwinStore store,
    StateObservationPublisher state,
    ObservedEventIntake intake,
    IOptions<GitHubOptions> options,
    TimeProvider timeProvider,
    ILogger<GitHubSyncService> logger) : IGitHubSyncService
{
    private static IReadOnlyList<string> ActiveStages => PlatformCatalog.DeliveryStages;
    private static readonly string[] AllWebhookEvents = ["*"];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRepoRefresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _archivedRepos = new(StringComparer.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub sync failed")]
    private partial void LogGitHubSyncFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to register GitHub webhook on {Repo}")]
    private partial void LogWebhookRegistrationFailed(Exception ex, string repo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping webhook registration while GitHub REST is paused: {Reason}")]
    private partial void LogWebhookRegistrationPaused(string reason);

    public async Task SyncAsync(bool fullBackfill, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = new IngestionCursor
        {
            SourceKey = "github",
            LastAttemptAt = now,
            Status = "running"
        };

        try
        {
            // 1. Always sync public GitOps desired state via raw.githubusercontent.com (0 API quota!)
            await SyncGitOpsDesiredStateAsync(now, ct);

            // Respect rate-limit guard before hitting api.github.com
            if (!fullBackfill && gh.IsRestPaused(out var pauseReason))
            {
                cursor.Status = "rate-limit-paused";
                cursor.LastError = pauseReason;
                await store.UpsertCursorAsync(cursor, ct);
                return;
            }

            // 2. Single batched GraphQL call for repo metadata, default commits, recent PRs, and releases across all cmdemo1-* repos
            var targetRepos = fullBackfill ? PlatformCatalog.ObservedRepositories : PlatformCatalog.Current.GitHub.PolledRepositories;
            var repoSnapshots = new List<RepositorySnapshot>();
            var prSnapshots = new List<SourceChangeSnapshot>();
            var artifactSnapshots = new List<ArtifactPackageSnapshot>();
            var newEvents = new List<ObservedEvent>();

            var gqlResult = await FetchBatchRepositoriesGraphQlAsync(targetRepos, fullBackfill, now, repoSnapshots, prSnapshots, artifactSnapshots, newEvents, ct);
            var graphQlRateLimited = gqlResult.RateLimited;
            var restRateLimited = false;

            // Preserve cached repository records when GraphQL is paused so REST webhook metadata still updates
            if (repoSnapshots.Count == 0)
            {
                var cachedRepos = await store.GetRepositoriesAsync(ct);
                var targetSet = targetRepos.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var cached in cachedRepos.Where(r => targetSet.Contains(r.Name)))
                {
                    repoSnapshots.Add(cached);
                }
            }

            // 3. ETag-cached REST calls for workflow runs (active repos only on incremental polls)
            var runSnapshots = new List<PipelineRunSnapshot>();
            var webhookStatuses = new List<WebhookRegistrationStatus>();
            var checkHooks = fullBackfill || !gh.IsRestBackoff();

            foreach (var repoName in targetRepos)
            {
                ct.ThrowIfCancellationRequested();
                if (gh.IsRestPaused(out _))
                {
                    restRateLimited = true;
                    break;
                }

                await SyncWorkflowRunsForRepoAsync(repoName, fullBackfill, now, runSnapshots, newEvents, ct);

                if (checkHooks)
                {
                    var hookStatus = await FetchWebhookStatusForRepoAsync(repoName, fullBackfill, now, ct);
                    if (hookStatus is not null)
                    {
                        webhookStatuses.Add(hookStatus);
                        var repoSnap = repoSnapshots.FirstOrDefault(r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));
                        if (repoSnap is not null)
                        {
                            repoSnap.WebhookRegistered = hookStatus.IsActive;
                            repoSnap.WebhookId = long.TryParse(hookStatus.ExternalId, out var wid) ? wid : null;
                            repoSnap.LastWebhookStatusCode = hookStatus.LastDeliveryStatusCode;
                            repoSnap.LastWebhookDeliveryAt = hookStatus.LastDeliveryAt;
                            repoSnap.UpdatedAt = now;
                        }
                    }
                }
            }

            if (repoSnapshots.Count > 0) await state.PublishAsync(StateKinds.Repository, repoSnapshots, EventSourceKind.GitHub, ct);
            if (prSnapshots.Count > 0) await state.PublishAsync(StateKinds.SourceChange, prSnapshots, EventSourceKind.GitHub, ct);
            if (runSnapshots.Count > 0) await state.PublishAsync(StateKinds.PipelineRun, runSnapshots, EventSourceKind.GitHub, ct);
            if (artifactSnapshots.Count > 0) await state.PublishAsync(StateKinds.Artifact, artifactSnapshots, EventSourceKind.GitHub, ct);

            // Save REST webhook statuses whenever REST was not rate-limited (independent of GraphQL quota)
            if (fullBackfill && webhookStatuses.Count > 0 && !restRateLimited)
            {
                await state.PublishSetAsync(StateKinds.WebhookStatus, "github", webhookStatuses, EventSourceKind.GitHub, ct);
            }

            // 4. Sync Work Items (status/workitems.json via 0-quota raw + Project 679 GraphQL)
            await SyncWorkItemsAsync(queryBoardGraphQl: fullBackfill, now, ct);

            var ingestedCount = await intake.PublishAsync(newEvents, EventSourceKind.GitHub, fullBackfill ? IngestionChannel.Backfill : IngestionChannel.Poll, ct);
            var quota = gh.GetStatus();

            cursor.LastSuccessAt = restRateLimited ? null : timeProvider.GetUtcNow();
            cursor.Status = restRateLimited || quota.Mode == "paused"
                ? "rate-limit-paused"
                : quota.Mode == "backoff"
                    ? "rate-limit-backoff"
                    : "ok";
            cursor.LastError = restRateLimited
                ? "GitHub REST rate limit reached; preserving cached state until reset"
                : graphQlRateLimited
                    ? $"GitHub GraphQL paused until {quota.GraphQlResetAt:HH:mm:ss}Z; REST runs, webhooks, and raw GitOps active"
                    : null;
            cursor.ItemsProcessed = repoSnapshots.Count + prSnapshots.Count + runSnapshots.Count + webhookStatuses.Count + ingestedCount;
            cursor.LastCursor = now.ToString("O", CultureInfo.InvariantCulture);
            await store.UpsertCursorAsync(cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogGitHubSyncFailed(ex);
            cursor.Status = "error";
            cursor.LastError = ex.Message;
            await store.UpsertCursorAsync(cursor, ct);
            throw;
        }
    }

    public async Task SyncBoardOnlyAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (gh.IsGraphQlPaused(out _))
        {
            return;
        }

        await SyncWorkItemsAsync(queryBoardGraphQl: true, now, ct);
    }

    public async Task SyncRepositoryAsync(string repositoryName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (_lastRepoRefresh.TryGetValue(repositoryName, out var last) && (now - last) < TimeSpan.FromSeconds(15))
        {
            return; // Coalesce burst webhooks on the same repo
        }
        _lastRepoRefresh[repositoryName] = now;

        if (repositoryName.Equals(PlatformCatalog.SystemRepository, StringComparison.OrdinalIgnoreCase))
        {
            await SyncGitOpsDesiredStateAsync(now, ct);
        }

        if (gh.IsRestPaused(out _))
        {
            return;
        }

        var repoSnapshots = new List<RepositorySnapshot>();
        var prSnapshots = new List<SourceChangeSnapshot>();
        var artifactSnapshots = new List<ArtifactPackageSnapshot>();
        var runSnapshots = new List<PipelineRunSnapshot>();
        var newEvents = new List<ObservedEvent>();

        await FetchBatchRepositoriesGraphQlAsync([repositoryName], fullBackfill: false, now, repoSnapshots, prSnapshots, artifactSnapshots, newEvents, ct);
        await SyncWorkflowRunsForRepoAsync(repositoryName, fullBackfill: false, now, runSnapshots, newEvents, ct);

        if (repoSnapshots.Count > 0) await state.PublishAsync(StateKinds.Repository, repoSnapshots, EventSourceKind.GitHub, ct);
        if (prSnapshots.Count > 0) await state.PublishAsync(StateKinds.SourceChange, prSnapshots, EventSourceKind.GitHub, ct);
        if (runSnapshots.Count > 0) await state.PublishAsync(StateKinds.PipelineRun, runSnapshots, EventSourceKind.GitHub, ct);
        if (artifactSnapshots.Count > 0) await state.PublishAsync(StateKinds.Artifact, artifactSnapshots, EventSourceKind.GitHub, ct);

        await intake.PublishAsync(newEvents, EventSourceKind.GitHub, IngestionChannel.Poll, ct);
    }

    public async Task<IReadOnlyList<WebhookRegistrationStatus>> EnsureWebhooksRegisteredAsync(string twinPublicBaseUrl, CancellationToken ct)
    {
        var baseUrl = twinPublicBaseUrl.TrimEnd('/');
        var targetUrl = $"{baseUrl}/hooks/github";
        var secret = options.Value.WebhookSecret;
        var results = new List<WebhookRegistrationStatus>();

        if (string.IsNullOrWhiteSpace(options.Value.Token) || string.IsNullOrWhiteSpace(secret))
        {
            return results;
        }

        if (gh.IsRestPaused(out var pauseReason))
        {
            LogWebhookRegistrationPaused(pauseReason);
            return await store.GetWebhookStatusesAsync(ct);
        }

        var now = timeProvider.GetUtcNow();
        foreach (var repo in PlatformCatalog.ObservedRepositories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var meta = await gh.GetApiAsync($"repos/{PlatformCatalog.GitHubOrg}/{repo}", bypassPause: false, ct);
                if (meta.RateLimited) break;
                if (meta.Success && !string.IsNullOrWhiteSpace(meta.Body))
                {
                    using var metaDoc = JsonDocument.Parse(meta.Body);
                    if (metaDoc.RootElement.TryGetProperty("archived", out var arch) && arch.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }
                }

                var hooksRes = await gh.GetApiAsync($"repos/{PlatformCatalog.GitHubOrg}/{repo}/hooks", bypassPause: false, ct);
                if (hooksRes.RateLimited) break;

                long? existingId = null;
                bool alreadyActiveAndMatching = false;
                if (hooksRes.Success && !string.IsNullOrWhiteSpace(hooksRes.Body))
                {
                    using var doc = JsonDocument.Parse(hooksRes.Body);
                    foreach (var hook in doc.RootElement.EnumerateArray())
                    {
                        var url = hook.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("url", out var u) ? u.GetString() : null;
                        var active = hook.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrWhiteSpace(url) &&
                            (url.Equals(targetUrl, StringComparison.OrdinalIgnoreCase) ||
                             (url.EndsWith("/hooks/github", StringComparison.OrdinalIgnoreCase) && url.Contains(PlatformCatalog.Current.GitHub.TwinHookUrlMarker, StringComparison.OrdinalIgnoreCase))))
                        {
                            existingId = hook.GetProperty("id").GetInt64();
                            alreadyActiveAndMatching = active && url.Equals(targetUrl, StringComparison.OrdinalIgnoreCase);
                            break;
                        }
                    }
                }

                if (alreadyActiveAndMatching && existingId is not null)
                {
                    results.Add(new WebhookRegistrationStatus
                    {
                        Id = $"github:{repo}",
                        Source = "github",
                        Target = repo,
                        EndpointUrl = targetUrl,
                        IsActive = true,
                        ExternalId = existingId.Value.ToString(CultureInfo.InvariantCulture),
                        Detail = "active",
                        CheckedAt = now
                    });
                    continue;
                }

                var payload = new
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
                };

                using var response = existingId is null
                    ? await gh.SendMutationAsync(HttpMethod.Post, $"repos/{PlatformCatalog.GitHubOrg}/{repo}/hooks", payload, ct)
                    : await gh.SendMutationAsync(HttpMethod.Patch, $"repos/{PlatformCatalog.GitHubOrg}/{repo}/hooks/{existingId.Value}", payload, ct);

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
                    Detail = response.IsSuccessStatusCode ? "registered" : $"HTTP {(int)response.StatusCode}",
                    CheckedAt = now
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogWebhookRegistrationFailed(ex, repo);
            }
        }

        if (results.Count > 0)
        {
            await state.PublishSetAsync(StateKinds.WebhookStatus, "github", results, EventSourceKind.GitHub, ct);
        }
        return results;
    }

    private static GitOpsStatusFeeds Feeds => PlatformCatalog.Current.GitHub.StatusFeeds;

    private Task<string?> FeedAsync(string branch, string path, CancellationToken ct)
        => string.IsNullOrWhiteSpace(path) ? Task.FromResult<string?>(null) : gh.GetRawPublicAsync(PlatformCatalog.GitHubOrg, PlatformCatalog.SystemRepository, branch, path, ct);

    private async Task SyncGitOpsDesiredStateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var systemJson = await FeedAsync(Feeds.DesiredStateBranch, Feeds.SystemJson, ct) ?? "{}";
        var pinned = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var stage in ActiveStages)
        {
            var vJson = await FeedAsync(Feeds.DesiredStateBranch, Feeds.StageVersions.Replace("{stage}", stage, StringComparison.Ordinal), ct);
            if (!string.IsNullOrWhiteSpace(vJson))
            {
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(vJson);
                    if (map is not null) pinned[stage] = map;
                }
                catch (JsonException) { }
            }
        }

        var costJson = await FeedAsync(Feeds.StatusBranch, Feeds.Cost, ct);
        var containersJson = await FeedAsync(Feeds.StatusBranch, Feeds.Containers, ct);

        await state.PublishAsync(StateKinds.DesiredConfig, [new DesiredSystemConfigSnapshot
        {
            Id = PlatformCatalog.SystemSlug,
            SystemName = PlatformCatalog.SystemName,
            SystemJson = systemJson,
            PinnedVersionsJson = JsonSerializer.Serialize(pinned),
            NodesJson = StableJson.Without(containersJson, "generated"),
            CostJson = costJson,
            UpdatedAt = now
        }], EventSourceKind.GitOps, ct);

        if (!string.IsNullOrWhiteSpace(costJson))
        {
            var existingStages = (await store.GetEnvironmentInstancesAsync(ct))
                .ToDictionary(s => s.StageName, StringComparer.OrdinalIgnoreCase);

            using var costDoc = JsonDocument.Parse(costJson);
            if (costDoc.RootElement.TryGetProperty("environments", out var envCosts) && envCosts.ValueKind == JsonValueKind.Array)
            {
                var updated = new List<EnvironmentInstanceSnapshot>();
                foreach (var stageDef in PlatformCatalog.Stages)
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
                await state.PublishAsync(StateKinds.Environment, updated, EventSourceKind.GitHub, ct);
            }
        }
    }

    private async Task<GitHubFetchResult> FetchBatchRepositoriesGraphQlAsync(
        IReadOnlyList<string> repos,
        bool fullBackfill,
        DateTimeOffset now,
        List<RepositorySnapshot> repoSnapshots,
        List<SourceChangeSnapshot> prSnapshots,
        List<ArtifactPackageSnapshot> artifactSnapshots,
        List<ObservedEvent> newEvents,
        CancellationToken ct)
    {
        if (repos.Count == 0)
        {
            return new GitHubFetchResult(true, false, false, System.Net.HttpStatusCode.OK, null);
        }

        var commitsCount = fullBackfill ? 6 : 2;
        var prsCount = fullBackfill ? 15 : 6;
        var aliases = string.Join("\n  ", repos.Select((r, i) =>
            $"r{i}: repository(owner: \"{PlatformCatalog.GitHubOrg}\", name: \"{r}\") {{ ...RepoFields }}"));

        var query = $$"""
        query {
          {{aliases}}
        }
        fragment RepoFields on Repository {
          name
          url
          isPrivate
          isArchived
          issues(states: OPEN) { totalCount }
          defaultBranchRef {
            name
            target {
              ... on Commit {
                history(first: {{commitsCount}}) {
                  nodes {
                    oid
                    messageHeadline
                    committedDate
                    url
                    author { name }
                  }
                }
              }
            }
          }
          pullRequests(first: {{prsCount}}, states: [OPEN, CLOSED, MERGED], orderBy: { field: UPDATED_AT, direction: DESC }) {
            nodes {
              number
              title
              url
              state
              headRefName
              baseRefName
              createdAt
              updatedAt
              mergedAt
              author { login __typename }
              mergeCommit { oid }
            }
          }
          deployments(first: 5, orderBy: { field: CREATED_AT, direction: DESC }) {
            nodes {
              id
              environment
              createdAt
              commitOid
              ref { name }
            }
          }
        }
        """;

        var res = await gh.PostGraphQlAsync(query, null, ct);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Body))
        {
            return res;
        }

        var existingRepos = (await store.GetRepositoriesAsync(ct))
            .ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(res.Body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return res;
        }

        for (var i = 0; i < repos.Count; i++)
        {
            if (!data.TryGetProperty($"r{i}", out var repoEl) || repoEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var repoName = repoEl.GetProperty("name").GetString() ?? repos[i];
            var htmlUrl = repoEl.GetProperty("url").GetString() ?? "";
            var isPrivate = repoEl.TryGetProperty("isPrivate", out var priv) && priv.ValueKind == JsonValueKind.True;
            var isArchived = repoEl.TryGetProperty("isArchived", out var arch) && arch.ValueKind == JsonValueKind.True;
            _archivedRepos[repoName] = isArchived;
            var openIssues = repoEl.TryGetProperty("issues", out var iss) && iss.TryGetProperty("totalCount", out var ic) ? ic.GetInt32() : 0;

            var pipelineId = PlatformCatalog.ResolvePipelineIdFromRepository(repoName);
            var deployable = PlatformCatalog.ResolveDeployableFromRepository(repoName);
            var isTwin = repoName.Equals(PlatformCatalog.TwinRepository, StringComparison.OrdinalIgnoreCase);
            var isRetired = isArchived || PlatformCatalog.Current.GitHub.RetiredRepositories.Contains(repoName, StringComparer.OrdinalIgnoreCase);

            var defaultBranch = "main";
            string? latestSha = null;
            string? latestMsg = null;
            string? latestAuthor = null;
            DateTimeOffset? latestCommitAt = null;

            if (repoEl.TryGetProperty("defaultBranchRef", out var dbr) && dbr.ValueKind == JsonValueKind.Object)
            {
                defaultBranch = dbr.TryGetProperty("name", out var bn) ? bn.GetString() ?? "main" : "main";
                if (dbr.TryGetProperty("target", out var tgt) &&
                    tgt.TryGetProperty("history", out var hist) &&
                    hist.TryGetProperty("nodes", out var commitsArr))
                {
                    foreach (var c in commitsArr.EnumerateArray())
                    {
                        var sha = c.GetProperty("oid").GetString() ?? "";
                        var msg = c.TryGetProperty("messageHeadline", out var mh) ? mh.GetString() ?? "" : "";
                        var authorName = c.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object && au.TryGetProperty("name", out var an)
                            ? an.GetString() ?? "unknown"
                            : "unknown";
                        var commitAt = c.TryGetProperty("committedDate", out var cd) && DateTimeOffset.TryParse(cd.GetString(), out var dt)
                            ? dt.ToUniversalTime()
                            : now;
                        var commitUrl = c.TryGetProperty("url", out var cu) ? cu.GetString() : htmlUrl;

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

            var openPrs = 0;
            if (repoEl.TryGetProperty("pullRequests", out var prsConn) &&
                prsConn.TryGetProperty("nodes", out var prsArr))
            {
                foreach (var pr in prsArr.EnumerateArray())
                {
                    var num = pr.GetProperty("number").GetInt32();
                    var state = (pr.GetProperty("state").GetString() ?? "OPEN").ToLowerInvariant();
                    if (state == "open") openPrs++;

                    var title = pr.GetProperty("title").GetString() ?? "";
                    var author = pr.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object && au.TryGetProperty("login", out var al)
                        ? al.GetString() ?? "unknown"
                        : "unknown";
                    var authorType = pr.TryGetProperty("author", out var au2) && au2.ValueKind == JsonValueKind.Object && au2.TryGetProperty("__typename", out var at)
                        ? at.GetString() ?? ""
                        : "";
                    var branch = pr.TryGetProperty("headRefName", out var hr) ? hr.GetString() ?? "" : "";
                    var baseBranch = pr.TryGetProperty("baseRefName", out var br) ? br.GetString() ?? defaultBranch : defaultBranch;
                    var mergeSha = pr.TryGetProperty("mergeCommit", out var mc) && mc.ValueKind == JsonValueKind.Object && mc.TryGetProperty("oid", out var oid)
                        ? oid.GetString()
                        : null;
                    var createdAt = pr.TryGetProperty("createdAt", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    var updatedAt = pr.TryGetProperty("updatedAt", out var ua) && DateTimeOffset.TryParse(ua.GetString(), out var udt) ? udt.ToUniversalTime() : now;
                    var mergedAt = pr.TryGetProperty("mergedAt", out var ma) && ma.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ma.GetString(), out var mdt)
                        ? mdt.ToUniversalTime()
                        : (DateTimeOffset?)null;

                    prSnapshots.Add(new SourceChangeSnapshot
                    {
                        Id = $"{repoName}#{num}",
                        Repository = repoName,
                        Number = num,
                        Title = title,
                        Url = pr.TryGetProperty("url", out var pu) ? pu.GetString() ?? "" : "",
                        State = state,
                        HeadBranch = branch,
                        BaseBranch = baseBranch,
                        Author = author,
                        IsBot = authorType == "Bot" || author.Contains("bot", StringComparison.OrdinalIgnoreCase) || author.Contains("cursor", StringComparison.OrdinalIgnoreCase) || author.Contains("copilot", StringComparison.OrdinalIgnoreCase),
                        PipelineId = pipelineId,
                        LinkedIssueNumber = ExtractIssueNumber(title, branch),
                        MergeCommitSha = mergeSha,
                        CreatedAt = createdAt,
                        UpdatedAt = updatedAt,
                        MergedAt = mergedAt
                    });
                }
            }

            if (repoEl.TryGetProperty("deployments", out var depConn) &&
                depConn.TryGetProperty("nodes", out var depArr))
            {
                foreach (var dep in depArr.EnumerateArray())
                {
                    var env = dep.TryGetProperty("environment", out var e) ? e.GetString() ?? "release" : "release";
                    var sha = dep.TryGetProperty("commitOid", out var s) ? s.GetString() ?? "" : "";
                    var refName = dep.TryGetProperty("ref", out var rf) && rf.ValueKind == JsonValueKind.Object && rf.TryGetProperty("name", out var rn)
                        ? rn.GetString() ?? ""
                        : "";
                    var depId = dep.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                    var createdAt = dep.TryGetProperty("createdAt", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    artifactSnapshots.Add(new ArtifactPackageSnapshot
                    {
                        Id = $"GitHubDeployment:{repoName}:{depId}",
                        Kind = "GitHubReleaseDeployment",
                        Name = $"{repoName}/{env}",
                        Version = string.IsNullOrEmpty(refName) ? (sha.Length >= 7 ? sha[..7] : depId) : refName,
                        RegistryOrFeed = "github-deployments",
                        CommitSha = sha,
                        PipelineId = pipelineId,
                        Url = $"{htmlUrl}/deployments",
                        PublishedAt = createdAt
                    });
                }
            }

            existingRepos.TryGetValue(repoName, out var prev);
            repoSnapshots.Add(new RepositorySnapshot
            {
                Name = repoName,
                FullName = $"{PlatformCatalog.GitHubOrg}/{repoName}",
                Url = htmlUrl,
                DefaultBranch = defaultBranch,
                IsPrivate = isPrivate,
                IsRetired = isRetired,
                IsTwinObserver = isTwin,
                PipelineId = pipelineId,
                LatestCommitSha = latestSha ?? prev?.LatestCommitSha,
                LatestCommitMessage = latestMsg ?? prev?.LatestCommitMessage,
                LatestCommitAuthor = latestAuthor ?? prev?.LatestCommitAuthor,
                LatestCommitAt = latestCommitAt ?? prev?.LatestCommitAt,
                OpenPullRequestCount = openPrs,
                OpenIssueCount = openIssues,
                WebhookRegistered = prev?.WebhookRegistered ?? false,
                WebhookId = prev?.WebhookId,
                LastWebhookStatusCode = prev?.LastWebhookStatusCode,
                LastWebhookDeliveryAt = prev?.LastWebhookDeliveryAt,
                UpdatedAt = now
            });
        }

        return res;
    }

    private async Task SyncWorkflowRunsForRepoAsync(
        string repoName,
        bool fullBackfill,
        DateTimeOffset now,
        List<PipelineRunSnapshot> runSnapshots,
        List<ObservedEvent> newEvents,
        CancellationToken ct)
    {
        var runsCount = fullBackfill ? 15 : 8;
        var res = await gh.GetApiAsync($"repos/{PlatformCatalog.GitHubOrg}/{repoName}/actions/runs?per_page={runsCount}", bypassPause: false, ct);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Body))
        {
            return;
        }

        // If response came from 304 ETag cache during incremental poll, skip re-writing snapshots
        if (res.FromCache304 && !fullBackfill)
        {
            return;
        }

        var pipelineId = PlatformCatalog.ResolvePipelineIdFromRepository(repoName);
        var deployable = PlatformCatalog.ResolveDeployableFromRepository(repoName);

        using var runsDoc = JsonDocument.Parse(res.Body);
        if (!runsDoc.RootElement.TryGetProperty("workflow_runs", out var runsArr) || runsArr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

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

    private async Task<WebhookRegistrationStatus?> FetchWebhookStatusForRepoAsync(
        string repoName,
        bool includeDeliveries,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var res = await gh.GetApiAsync($"repos/{PlatformCatalog.GitHubOrg}/{repoName}/hooks", bypassPause: false, ct);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Body))
        {
            return null;
        }

        var webhookRegistered = false;
        long? webhookId = null;
        int? lastDeliveryStatus = null;
        DateTimeOffset? lastDeliveryAt = null;
        string? hookUrl = null;

        using var hooksDoc = JsonDocument.Parse(res.Body);
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

                    if (includeDeliveries)
                    {
                        var delRes = await gh.GetApiAsync($"repos/{PlatformCatalog.GitHubOrg}/{repoName}/hooks/{webhookId.Value}/deliveries?per_page=1", bypassPause: false, ct);
                        if (delRes.Success && !string.IsNullOrWhiteSpace(delRes.Body))
                        {
                            using var delDoc = JsonDocument.Parse(delRes.Body);
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

        // Archived repositories cannot hold webhooks, so they are not part of the hook inventory.
        if (!webhookRegistered && webhookId is null && await IsArchivedAsync(repoName, ct))
        {
            return null;
        }

        return new WebhookRegistrationStatus
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
        };
    }

    private async Task<bool> IsArchivedAsync(string repoName, CancellationToken ct)
    {
        if (_archivedRepos.TryGetValue(repoName, out var known))
        {
            return known;
        }

        var meta = await gh.GetApiAsync($"repos/{PlatformCatalog.GitHubOrg}/{repoName}", bypassPause: false, ct);
        if (!meta.Success || string.IsNullOrWhiteSpace(meta.Body))
        {
            return false;
        }
        using var doc = JsonDocument.Parse(meta.Body);
        var archived = doc.RootElement.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
        _archivedRepos[repoName] = archived;
        return archived;
    }

    private async Task SyncWorkItemsAsync(bool queryBoardGraphQl, DateTimeOffset now, CancellationToken ct)
    {
        var itemsById = new Dictionary<string, WorkItemSnapshot>(StringComparer.OrdinalIgnoreCase);

        // 0-quota raw read of cmdemo1-system status/workitems.json
        var workItemsJson = await FeedAsync(Feeds.StatusBranch, Feeds.WorkItems, ct);
        if (!string.IsNullOrWhiteSpace(workItemsJson))
        {
            using var doc = JsonDocument.Parse(workItemsJson);
            if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray().Take(150))
                {
                    var number = item.GetProperty("number").GetInt32();
                    var id = $"{PlatformCatalog.Current.GitHub.DefaultWorkItemRepository}#{number}";
                    var createdAt = item.TryGetProperty("createdAt", out var ca) && DateTimeOffset.TryParse(ca.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    var closedAt = item.TryGetProperty("closedAt", out var cla) && cla.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(cla.GetString(), out var cldt) ? cldt.ToUniversalTime() : (DateTimeOffset?)null;
                    var envs = item.TryGetProperty("environments", out var envEl) ? envEl : default;

                    itemsById[id] = new WorkItemSnapshot
                    {
                        Id = id,
                        Repository = PlatformCatalog.Current.GitHub.DefaultWorkItemRepository,
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
                        TddVersion = GetNestedString(envs, PlatformCatalog.FirstStage, "version"),
                        TddDeployedAt = GetNestedDate(envs, PlatformCatalog.FirstStage, "deployedAt"),
                        UatVersion = DeliveryStageSlots.MiddleStage is { } middle ? GetNestedString(envs, middle, "version") : null,
                        UatDeployedAt = DeliveryStageSlots.MiddleStage is { } middleStage ? GetNestedDate(envs, middleStage, "deployedAt") : null,
                        ProdVersion = GetNestedString(envs, PlatformCatalog.ProductionStage, "version"),
                        ProdDeployedAt = GetNestedDate(envs, PlatformCatalog.ProductionStage, "deployedAt"),
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

        await AddRepositoryIssuesAsync(itemsById, now, ct);

        if (!queryBoardGraphQl || gh.IsGraphQlPaused(out _))
        {
            // The board owns column, state and timing: keep its last observation instead of overwriting it with
            // workitems.json values, which would make every item flap between the two producers.
            var current = (await store.GetWorkItemsAsync(ct)).ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var item in itemsById.Values)
            {
                if (current.TryGetValue(item.Id, out var known) && known.ColumnSource == "project")
                {
                    item.Column = known.Column;
                    item.ColumnSource = known.ColumnSource;
                    item.State = known.State;
                    item.ClosedAt = known.ClosedAt;
                    item.UpdatedAt = known.UpdatedAt;
                }
            }
            if (itemsById.Count > 0) await state.PublishAsync(StateKinds.WorkItem, WithTimings(itemsById.Values), EventSourceKind.GitHub, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(PlatformCatalog.WorkItemsProjectNodeId))
        {
            return;
        }

        // Slim GraphQL query (2 pages max = 200 newest board items)
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
                      author { login }
                      assignees(first: 1) { nodes { login } }
                    }
                  }
                }
              }
            }
          }
        }
        """;

        string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var resp = await gh.PostGraphQlAsync(graphqlQuery, new { nodeId = PlatformCatalog.WorkItemsProjectNodeId, cursor }, ct);
            if (!resp.Success || string.IsNullOrWhiteSpace(resp.Body))
            {
                break;
            }

            using var gDoc = JsonDocument.Parse(resp.Body);
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
                        ? rn.GetString() ?? PlatformCatalog.Current.GitHub.DefaultWorkItemRepository
                        : PlatformCatalog.Current.GitHub.DefaultWorkItemRepository;
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
                        var author = issue.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object && au.TryGetProperty("login", out var aul) ? aul.GetString() : null;
                        var assignee = issue.TryGetProperty("assignees", out var asg) && asg.TryGetProperty("nodes", out var asn) && asn.ValueKind == JsonValueKind.Array && asn.GetArrayLength() > 0
                            && asn[0].TryGetProperty("login", out var asl) ? asl.GetString() : null;
                        var (workerKind, agent) = Worker(author, assignee);
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
                            WorkerKind = workerKind,
                            Agent = agent,
                            Assignee = assignee,
                            CreatedAt = createdAt,
                            ClosedAt = closedAt,
                            UpdatedAt = updatedAt
                        };
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

        if (itemsById.Count > 0)
        {
            await state.PublishAsync(StateKinds.WorkItem, WithTimings(itemsById.Values), EventSourceKind.GitHub, ct);
        }
    }

    private static readonly TimeSpan IssueSweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IssueSweepInitialWindow = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _issuesSince = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastIssueSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// Issues from each observed repository, so work that is not on Project 679 and not in workitems.json (e.g.
    /// cmdemo1-dashboard#1011) still reaches the twin. Items the board or workitems.json already produced keep their
    /// richer data. Swept every 10 minutes with an "updated since" cursor; unchanged pages are ETag 304s.
    /// </summary>
    private async Task AddRepositoryIssuesAsync(Dictionary<string, WorkItemSnapshot> itemsById, DateTimeOffset now, CancellationToken ct)
    {
        if (now - _lastIssueSweep < IssueSweepInterval)
        {
            return;
        }
        _lastIssueSweep = now;
        foreach (var repo in PlatformCatalog.Current.GitHub.PolledRepositories)
        {
            var since = _issuesSince.GetValueOrDefault(repo, now - IssueSweepInitialWindow);
            for (var page = 1; page <= 3; page++)
            {
                var res = await gh.GetApiAsync(
                    $"repos/{PlatformCatalog.GitHubOrg}/{repo}/issues?state=all&sort=updated&direction=asc&per_page=100&page={page}&since={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
                    bypassPause: false, ct);
                if (!res.Success || string.IsNullOrWhiteSpace(res.Body))
                {
                    break;
                }
                using var doc = JsonDocument.Parse(res.Body);
                var count = 0;
                foreach (var issue in doc.RootElement.EnumerateArray())
                {
                    count++;
                    if (issue.TryGetProperty("pull_request", out _))
                    {
                        continue;
                    }
                    var item = FromIssue(repo, issue, now);
                    if (item.UpdatedAt > since)
                    {
                        _issuesSince[repo] = item.UpdatedAt;
                    }
                    itemsById.TryAdd(item.Id, item);
                }
                if (count < 100)
                {
                    break;
                }
            }
        }
    }

    private static WorkItemSnapshot FromIssue(string repo, JsonElement issue, DateTimeOffset now)
    {
        static DateTimeOffset? Date(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d.ToUniversalTime() : null;
        var number = issue.GetProperty("number").GetInt32();
        var state = issue.TryGetProperty("state", out var st) ? st.GetString() ?? "open" : "open";
        var author = issue.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() : null;
        var assignee = issue.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var al) ? al.GetString() : null;
        var (workerKind, agent) = Worker(author, assignee);
        var createdAt = Date(issue, "created_at") ?? now;
        return new WorkItemSnapshot
        {
            Id = $"{repo}#{number}",
            Repository = repo,
            Number = number,
            Title = issue.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Url = issue.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            State = state,
            Column = state == "closed" ? "Done" : assignee is null ? "Todo" : "In Progress",
            ColumnSource = "derived",
            WorkerKind = workerKind,
            Agent = agent,
            Assignee = assignee,
            CreatedAt = createdAt,
            ClosedAt = Date(issue, "closed_at"),
            UpdatedAt = Date(issue, "updated_at") ?? createdAt
        };
    }

    /// <summary>
    /// Who works an issue, derived the same way by the board and the issue sweep: they observe the same item on alternate
    /// runs, so different answers made every such item flap between two observations (about 1,400 an hour).
    /// </summary>
    internal static (string WorkerKind, string? Agent) Worker(string? author, string? assignee)
    {
        var bot = (author ?? "").Contains("bot", StringComparison.OrdinalIgnoreCase) ? author
            : (assignee ?? "").Contains("bot", StringComparison.OrdinalIgnoreCase) ? assignee
            : null;
        return bot is null ? ("team", null) : ("bot", bot);
    }

    /// <summary>Lead and cycle times, computed the same way whichever path observed the item.</summary>
    private static List<WorkItemSnapshot> WithTimings(IEnumerable<WorkItemSnapshot> items)
    {
        var list = items.ToList();
        foreach (var item in list)
        {
            item.LeadTimeHours = item.ProdDeployedAt is null ? null : Math.Round((item.ProdDeployedAt.Value - item.CreatedAt).TotalHours, 1);
            item.CycleTimeHours = item.ClosedAt is null ? null : Math.Round((item.ClosedAt.Value - item.CreatedAt).TotalHours, 1);
        }
        return list;
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
