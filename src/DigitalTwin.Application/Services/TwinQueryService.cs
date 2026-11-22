using System.Globalization;
using System.Text.Json;
using DigitalTwin.Core.Ports;
using DigitalTwin.Application.Models;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Measurement;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.ReliabilityAndDrift;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Application.Services;

public sealed class TwinQueryService(
    ITwinStore store,
    TwinTimeTravel timeTravel,
    IGitHubRateLimitProvider rateLimitProvider,
    IIntakeQueueMonitor intakeMonitor)
{
    private static readonly string[] ActiveStageNames = ["tdd", "uat", "prod"];
    private const int DashboardChainCount = 6;
    private const int MaxDashboardPullRequestChains = 2;
    private static readonly string[] BoardWipColumns = ["Todo", "In Progress", "Review", "Merged", "TDD", "UAT"];

    public async Task<TwinSummaryDto> GetSummaryAsync(DateTimeOffset? asOf, CancellationToken ct)
        => await GetSummaryAsync(await timeTravel.ResolveAsync(asOf, ct), ct);

    private async Task<TwinSummaryDto> GetSummaryAsync(TwinReadScope scope, CancellationToken ct)
    {
        var model = scope.Model;
        var now = scope.Now;
        var desired = await model.GetDesiredConfigAsync(ct);
        var pinned = DriftEvaluator.ParsePinnedVersions(desired?.PinnedVersionsJson);
        var repos = await model.GetRepositoriesAsync(ct);
        var prs = await model.GetSourceChangesAsync(ct);
        var runs = await model.GetPipelineRunsAsync(200, ct);
        var deliveries = await model.GetDeliveriesAsync(300, ct);
        var stages = await model.GetEnvironmentInstancesAsync(ct);
        var workloads = await model.GetWorkloadsAsync(ct);
        var probes = await model.GetHealthProbesAsync(ct);
        var drifts = await model.GetDriftFindingsAsync(includeResolved: false, ct);
        var workItems = await model.GetWorkItemsAsync(ct);
        var cursors = await model.GetCursorsAsync(ct);
        var webhooks = await model.GetWebhookStatusesAsync(ct);
        var intakeQueue = await intakeMonitor.GetStatusAsync(ct);

        var pipelines = Cmdemo1PlatformCatalog.Pipelines.Select(def =>
        {
            var repo = repos.FirstOrDefault(r => r.Name.Equals(def.Repository, StringComparison.OrdinalIgnoreCase));
            var stageVersions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var stage in ActiveStageNames)
            {
                string? v = null;
                if (def.DeployableName is not null &&
                    pinned.TryGetValue(stage, out var map) &&
                    map.TryGetValue(def.DeployableName, out var pinnedVer))
                {
                    v = pinnedVer;
                }
                else if (def.DeployableName is not null)
                {
                    v = deliveries
                        .Where(d => d.StageName.Equals(stage, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(d.DeployableName, def.DeployableName, StringComparison.OrdinalIgnoreCase)
                                 && d.State == "succeeded")
                        .OrderByDescending(d => d.FinishedAt)
                        .Select(d => d.ReleaseVersion)
                        .FirstOrDefault();
                }
                stageVersions[stage] = v;
            }

            var latestRun = runs
                .Where(r => r.PipelineId.Equals(def.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.UpdatedAt)
                .FirstOrDefault();

            var openPrCount = prs.Count(p => p.PipelineId.Equals(def.Id, StringComparison.OrdinalIgnoreCase) && p.State == "open");
            var activeDeployments = deliveries.Count(d => d.PipelineId.Equals(def.Id, StringComparison.OrdinalIgnoreCase) && d.State is "queued" or "executing" or "waiting");

            return new PipelineSummaryDto(def, stageVersions, latestRun, openPrCount, activeDeployments, repo);
        }).ToList();

        var stageSummaries = Cmdemo1PlatformCatalog.Stages.Select(stageDef =>
        {
            var inst = stages.FirstOrDefault(s => s.StageName.Equals(stageDef.Name, StringComparison.OrdinalIgnoreCase));
            var stageWorkloads = workloads.Where(w => w.StageName.Equals(stageDef.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var stageProbes = probes.Where(p => p.StageName.Equals(stageDef.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var stageDeliveries = deliveries.Where(d => d.StageName.Equals(stageDef.Name, StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
            var stageDrifts = drifts.Where(dr => dr.StageName.Equals(stageDef.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            return new StageSummaryDto(stageDef, inst, stageWorkloads, stageProbes, stageDeliveries, stageDrifts);
        }).ToList();

        var workSummary = ComputeWorkSummary(workItems);
        var dora7 = DoraMetricsCalculator.Calculate(deliveries, workSummary.ReworkRate, now, 7, "prod");
        var dora28 = DoraMetricsCalculator.Calculate(deliveries, workSummary.ReworkRate, now, 28, "prod");

        var (costMtd, costYesterday) = ParseSystemCost(desired?.CostJson);

        return new TwinSummaryDto(
            PlatformName: Cmdemo1PlatformCatalog.PlatformName,
            SystemSlug: Cmdemo1PlatformCatalog.SystemSlug,
            SystemName: Cmdemo1PlatformCatalog.SystemName,
            GeneratedAt: now,
            Pipelines: pipelines,
            Stages: stageSummaries,
            Dora7Days: dora7,
            Dora28Days: dora28,
            WorkSummary: workSummary,
            ActiveDriftCount: drifts.Count,
            CriticalDriftCount: drifts.Count(d => d.Severity == DriftSeverity.Critical),
            IngestionCursors: cursors,
            Webhooks: webhooks,
            GitHubQuota: rateLimitProvider.GetStatus(),
            IntakeQueue: intakeQueue,
            CostMonthToDateUsd: costMtd,
            CostYesterdayUsd: costYesterday,
            AsOf: scope.AsOf);
    }

    public Task<IntakeQueueStatus> GetIntakeQueueAsync(CancellationToken ct) => intakeMonitor.GetStatusAsync(ct);

    public async Task<TwinGraphDto> GetGraphAsync(DateTimeOffset? asOf, CancellationToken ct)
    {
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var summary = await GetSummaryAsync(scope, ct);
        var resources = await scope.Model.GetAzureResourcesAsync(ct);
        var nodes = new List<GraphNodeDto>();
        var edges = new List<GraphEdgeDto>();

        nodes.Add(new GraphNodeDto("platform:cm", "Platform", Cmdemo1PlatformCatalog.PlatformName, "One level above observed systems", "healthy",
            new Dictionary<string, string?> { ["role"] = "Platform" }));
        nodes.Add(new GraphNodeDto("system:cmdemo1", "System", Cmdemo1PlatformCatalog.SystemSlug, Cmdemo1PlatformCatalog.SystemName,
            summary.CriticalDriftCount > 0 ? "degraded" : "healthy",
            new Dictionary<string, string?> { ["octopusSpace"] = Cmdemo1PlatformCatalog.OctopusSpaceId, ["gitopsRepo"] = Cmdemo1PlatformCatalog.SystemRepository }));
        edges.Add(new GraphEdgeDto("platform:cm", "system:cmdemo1", "OBSERVES"));

        foreach (var p in summary.Pipelines)
        {
            var nodeId = $"pipeline:{p.Definition.Id}";
            var status = p.Definition.IsRetired ? "retired" : p.LatestRun?.Conclusion == "failure" ? "failed" : "healthy";
            nodes.Add(new GraphNodeDto(nodeId, p.Definition.IsTwinObserver ? "PlatformObserver" : "Pipeline",
                p.Definition.Name, p.Definition.Repository, status,
                new Dictionary<string, string?> { ["kind"] = p.Definition.Kind, ["deployable"] = p.Definition.DeployableName }));
            edges.Add(new GraphEdgeDto(p.Definition.IsTwinObserver ? "platform:cm" : "system:cmdemo1", nodeId,
                p.Definition.IsTwinObserver ? "HOSTS_OBSERVER" : "HAS_PIPELINE"));
        }

        foreach (var hb in Cmdemo1PlatformCatalog.HostingBoundaries)
        {
            var hbId = $"boundary:{hb.Name}";
            nodes.Add(new GraphNodeDto(hbId, "HostingBoundary", hb.Name, $"{hb.Kind} ({hb.Location})", "healthy",
                new Dictionary<string, string?> { ["resourceGroup"] = hb.ResourceGroup }));
        }

        foreach (var st in summary.Stages)
        {
            var stId = $"stage:{st.Stage.Name}";
            var status = st.Stage.IsDeclaredOnly ? "declared" : st.ActiveDrifts.Any(d => d.Severity == DriftSeverity.Critical) ? "drifted" : "healthy";
            nodes.Add(new GraphNodeDto(stId, "DeploymentStage", st.Stage.Name, st.Stage.Tier, status,
                new Dictionary<string, string?> { ["hostingBoundary"] = st.Stage.HostingBoundary, ["resourceGroup"] = st.Stage.ResourceGroup }));
            edges.Add(new GraphEdgeDto("system:cmdemo1", stId, "HAS_STAGE"));
            edges.Add(new GraphEdgeDto(stId, $"boundary:{st.Stage.HostingBoundary}", "HOSTED_ON"));

            foreach (var w in st.Workloads)
            {
                var wId = $"workload:{st.Stage.Name}:{w.DeployableName}";
                nodes.Add(new GraphNodeDto(wId, "Workload", w.ResourceName, $"{w.DeployableName} {w.RunningVersion ?? w.PinnedVersion}", w.HealthState ?? "Healthy",
                    new Dictionary<string, string?> { ["revision"] = w.ActiveRevisionName, ["fqdn"] = w.Fqdn }));
                edges.Add(new GraphEdgeDto(stId, wId, "RUNS_WORKLOAD"));
            }
        }

        foreach (var res in resources.Take(80))
        {
            if (res.IsTwinOwnResource)
            {
                edges.Add(new GraphEdgeDto("pipeline:twin-observer", $"az:{res.Name}", "HOSTED_BY"));
            }
        }

        return new TwinGraphDto(Cmdemo1PlatformCatalog.PlatformName, Cmdemo1PlatformCatalog.SystemSlug, nodes, edges);
    }

    public async Task<DashboardStateDto> GetDashboardStateAsync(DateTimeOffset? asOf, CancellationToken ct)
    {
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var model = scope.Model;
        var summary = await GetSummaryAsync(scope, ct);
        var events = await store.QueryEventsAsync(null, scope.AsOf, null, null, null, null, 100, ct);
        // The dashboard is public and never shows source payloads.
        foreach (var e in events)
        {
            e.PayloadJson = "{}";
        }
        var workItems = await model.GetWorkItemsAsync(ct);
        var prs = (await model.GetSourceChangesAsync(ct)).Where(p => p.State == "open").OrderByDescending(p => p.UpdatedAt).Take(40).ToList();
        var runs = await model.GetPipelineRunsAsync(60, ct);
        var deliveries = await model.GetDeliveriesAsync(80, ct);
        var runbooks = await model.GetRunbooksAsync(30, ct);
        var freezes = await model.GetFreezesAsync(ct);
        var drifts = await model.GetDriftFindingsAsync(includeResolved: false, ct);
        var resources = await model.GetAzureResourcesAsync(ct);
        var artifacts = await model.GetArtifactsAsync(ct);
        var chains = await BuildChainsAsync(scope, ct);
        // Only chains that reached prod in the last 28 days need their failure signals for DORA.
        var doraChains = await WithFailureSignalsAsync([.. chains.Where(c => c.Stages[^1].StartedAt >= scope.Now.AddDays(-28))], scope, ct);

        return new DashboardStateDto(
            summary,
            events,
            workItems.OrderByDescending(w => w.UpdatedAt).Take(120).ToList(),
            prs,
            runs,
            deliveries,
            runbooks,
            freezes,
            drifts,
            resources,
            artifacts,
            await timeTravel.GetHistoryStartAsync(ct),
            await WithSignalsAsync(SelectDashboardChains(chains), scope, ct),
            ChainDora.Calculate(doraChains, scope.Now, 7),
            ChainDora.Calculate(doraChains, scope.Now, 28));
    }

    /// <summary>DORA keys from change chains (deploy frequency, lead time, change failure rate, time to restore).</summary>
    public async Task<ChainDoraMetrics> GetChainDoraAsync(int windowDays, string? deployable, DateTimeOffset? asOf, CancellationToken ct)
    {
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var chains = await WithFailureSignalsAsync([.. await BuildChainsAsync(scope, ct)], scope, ct);
        return ChainDora.Calculate(chains, scope.Now, Math.Clamp(windowDays, 1, 90), string.IsNullOrWhiteSpace(deployable) ? null : deployable);
    }

    public async Task<ChangeChainsDto> GetChangeChainsAsync(string? query, int limit, DateTimeOffset? asOf, CancellationToken ct)
    {
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var chains = await BuildChainsAsync(scope, ct);
        var matches = string.IsNullOrWhiteSpace(query) ? chains : ChangeChainBuilder.Find(chains, query);
        var top = matches.Take(Math.Clamp(limit, 1, 100)).ToList();
        var timeline = !string.IsNullOrWhiteSpace(query) && top.Count > 0
            ? await store.GetEventsByChainAsync(top[0].CorrelationIds, scope.AsOf, 60, null, ct)
            : [];
        return new ChangeChainsDto(scope.AsOf, query, matches.Count, await WithSignalsAsync(top, scope, ct), timeline);
    }

    /// <summary>Reviews, failed jobs and checks, interventions and sign-offs from the journal, attached to their stages.</summary>
    private async Task<IReadOnlyList<ChangeChain>> WithSignalsAsync(List<ChangeChain> chains, TwinReadScope scope, CancellationToken ct)
    {
        if (chains.Count == 0)
        {
            return chains;
        }
        var ids = chains.SelectMany(c => c.CorrelationIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var events = await store.GetEventsByChainAsync(ids, scope.AsOf, 2000, ChangeChainSignals.EventTypes, ct);
        return ChangeChainSignals.Attach(chains, events);
    }

    private static readonly string[] ProdFailureEventTypes = ["twin.octopus.interruption.guidedFailure", "twin.octopus.deployment.failed"];

    /// <summary>DORA only needs prod failure signals, which Octopus correlates by release id: a small query for many chains.</summary>
    private async Task<IReadOnlyList<ChangeChain>> WithFailureSignalsAsync(List<ChangeChain> chains, TwinReadScope scope, CancellationToken ct)
    {
        var releaseIds = chains.Where(c => c.ReleaseId is not null).Select(c => c.ReleaseId!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (releaseIds.Count == 0)
        {
            return chains;
        }
        var events = await store.GetEventsByChainAsync(releaseIds, scope.AsOf, 2000, ProdFailureEventTypes, ct);
        return ChangeChainSignals.Attach(chains, events);
    }

    public static readonly IReadOnlyList<string> ConfigChangeEventTypes =
    [
        "twin.octopus.process.changed",
        "twin.octopus.variables.changed",
        "twin.octopus.project.changed",
        "twin.octopus.trigger.changed",
        "twin.octopus.runbook.changed"
    ];

    /// <summary>Octopus deployment process, variable, project, trigger and runbook changes, newest first.</summary>
    public async Task<IReadOnlyList<ConfigChangeDto>> GetConfigChangesAsync(DateTimeOffset? since, int limit, DateTimeOffset? asOf, CancellationToken ct)
    {
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var take = Math.Clamp(limit, 1, 500);
        var changes = new List<ObservedEvent>();
        foreach (var type in ConfigChangeEventTypes)
        {
            changes.AddRange(await store.QueryEventsAsync(since, scope.AsOf, EventSourceKind.Octopus, null, null, type, take, ct));
        }
        return changes.OrderByDescending(e => e.OccurredAt).Take(take)
            .Select(e => new ConfigChangeDto(e.OccurredAt, e.EventType["twin.octopus.".Length..].Replace(".changed", "", StringComparison.Ordinal),
                e.DeployableName, e.PipelineId, e.Summary, e.Actor, e.SourceUrl))
            .ToList();
    }

    /// <summary>Most recent changes, favouring ones that reached a build: the dashboard stays sparse.</summary>
    private static List<ChangeChain> SelectDashboardChains(IReadOnlyList<ChangeChain> chains)
    {
        var pullRequestOnly = chains.Where(c => c.Version is null).Take(MaxDashboardPullRequestChains);
        return chains.Where(c => c.Version is not null).Take(DashboardChainCount)
            .Concat(pullRequestOnly)
            .OrderByDescending(c => c.LastActivityAt)
            .Take(DashboardChainCount)
            .ToList();
    }

    private static async Task<IReadOnlyList<ChangeChain>> BuildChainsAsync(TwinReadScope scope, CancellationToken ct)
    {
        var model = scope.Model;
        var sources = new ChangeChainSources(
            await model.GetWorkItemsAsync(ct),
            await model.GetSourceChangesAsync(ct),
            await model.GetPipelineRunsAsync(1000, ct),
            await model.GetArtifactsAsync(ct),
            await model.GetReleasesAsync(500, ct),
            await model.GetDeliveriesAsync(1000, ct));
        return ChangeChainBuilder.Build(sources, scope.Now);
    }

    public async Task<PointInTimeStateDto> GetStateAtAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var summary = await GetSummaryAsync(scope, ct);
        var resources = await scope.Model.GetAzureResourcesAsync(ct);
        var workItems = await scope.Model.GetWorkItemsAsync(ct);
        var drift = await scope.Model.GetDriftFindingsAsync(includeResolved: false, ct);
        var historyStart = await timeTravel.GetHistoryStartAsync(ct);

        var environments = summary.Stages
            .Where(s => ActiveStageNames.Contains(s.Stage.Name, StringComparer.OrdinalIgnoreCase))
            .Select(s => new StageAtDto(
                s.Stage.Name,
                summary.Pipelines
                    .Where(p => p.Definition.DeployableName is not null)
                    .ToDictionary(p => p.Definition.DeployableName!, p => p.StageVersions.GetValueOrDefault(s.Stage.Name), StringComparer.OrdinalIgnoreCase),
                s.Workloads.Select(w => new WorkloadAtDto(w.DeployableName, w.RunningVersion, w.ActiveRevisionName, w.HealthState)).ToList(),
                s.RecentDeliveries.OrderByDescending(d => d.QueuedAt)
                    .Select(d => new DeliveryAtDto(d.ReleaseVersion, d.DeployableName, d.State, d.QueuedAt, d.FinishedAt))
                    .FirstOrDefault()))
            .ToList();

        return new PointInTimeStateDto(
            scope.Now,
            historyStart,
            historyStart is null || scope.Now < historyStart,
            environments,
            resources.Select(r => new AzureResourceAtDto(r.Name, r.Type, r.ResourceGroup)).ToList(),
            workItems.GroupBy(w => w.Column, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            workItems.Count(w => w.State == "open"),
            drift.Select(d => $"{d.Severity}: {d.Summary}").ToList());
    }

    /// <summary>What changed between two instants: versions and revisions per environment, Azure resources, drift, board.</summary>
    public async Task<EnvironmentComparisonDto> CompareAsync(DateTimeOffset from, DateTimeOffset? to, CancellationToken ct)
    {
        var a = await GetStateAtAsync(from, ct);
        var b = await GetStateAtAsync(to ?? DateTimeOffset.MaxValue, ct);

        static List<ValueChangeDto> Diff(IReadOnlyDictionary<string, string?> x, IReadOnlyDictionary<string, string?> y)
            => x.Keys.Union(y.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(k => new ValueChangeDto(k, x.GetValueOrDefault(k), y.GetValueOrDefault(k), !string.Equals(x.GetValueOrDefault(k), y.GetValueOrDefault(k), StringComparison.Ordinal)))
                .ToList();

        var stages = a.Environments.Select(e => e.Stage).Union(b.Environments.Select(e => e.Stage)).Select(stage =>
        {
            var x = a.Environments.FirstOrDefault(e => e.Stage == stage);
            var y = b.Environments.FirstOrDefault(e => e.Stage == stage);
            var versions = Diff(x?.DeployedVersions ?? new Dictionary<string, string?>(), y?.DeployedVersions ?? new Dictionary<string, string?>());
            var revisions = Diff(
                (x?.Workloads ?? []).ToDictionary(w => w.Deployable, w => w.Revision, StringComparer.OrdinalIgnoreCase),
                (y?.Workloads ?? []).ToDictionary(w => w.Deployable, w => w.Revision, StringComparer.OrdinalIgnoreCase));
            return new StageComparisonDto(stage, versions, revisions, versions.Any(v => v.Changed) || revisions.Any(r => r.Changed));
        }).ToList();

        var resA = a.AzureResources.Select(r => $"{r.ResourceGroup}/{r.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resB = b.AzureResources.Select(r => $"{r.ResourceGroup}/{r.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var board = Diff(a.BoardColumns.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase),
            b.BoardColumns.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase));

        return new EnvironmentComparisonDto(a.AsOf, b.AsOf, stages,
            resB.Except(resA).Order(StringComparer.OrdinalIgnoreCase).ToList(), resA.Except(resB).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            resA.Count, resB.Count, a.ActiveDrift, b.ActiveDrift, board);
    }

    public Task<IReadOnlyList<ObservedEvent>> QueryEventsAsync(
        DateTimeOffset? since,
        DateTimeOffset? until,
        EventSourceKind? source,
        string? pipelineId,
        string? stageName,
        string? eventType,
        int limit,
        CancellationToken ct)
        => store.QueryEventsAsync(since, until, source, pipelineId, stageName, eventType, Math.Clamp(limit, 1, 500), ct);

    public async Task<StageSummaryDto?> GetEnvironmentAsync(string stageName, DateTimeOffset? asOf, CancellationToken ct)
    {
        var summary = await GetSummaryAsync(asOf, ct);
        return summary.Stages.FirstOrDefault(s => s.Stage.Name.Equals(stageName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<DriftFinding>> GetDriftAsync(bool includeResolved, DateTimeOffset? asOf, CancellationToken ct)
        => await (await timeTravel.ResolveAsync(asOf, ct)).Model.GetDriftFindingsAsync(includeResolved, ct);

    public async Task<IReadOnlyList<WorkItemSnapshot>> GetWorkItemsAsync(string? column, string? worker, int limit, DateTimeOffset? asOf, CancellationToken ct)
    {
        var items = await (await timeTravel.ResolveAsync(asOf, ct)).Model.GetWorkItemsAsync(ct);
        return items
            .Where(i => string.IsNullOrWhiteSpace(column) || i.Column.Equals(column, StringComparison.OrdinalIgnoreCase))
            .Where(i => string.IsNullOrWhiteSpace(worker) || i.WorkerKind.Equals(worker, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    public async Task<IReadOnlyList<DeliveryExecutionSnapshot>> GetDeploymentsAsync(string? stage, string? deployable, int limit, DateTimeOffset? asOf, CancellationToken ct)
    {
        var all = await (await timeTravel.ResolveAsync(asOf, ct)).Model.GetDeliveriesAsync(500, ct);
        return all
            .Where(d => string.IsNullOrWhiteSpace(stage) || d.StageName.Equals(stage, StringComparison.OrdinalIgnoreCase))
            .Where(d => string.IsNullOrWhiteSpace(deployable) || string.Equals(d.DeployableName, deployable, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.QueuedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
    }

    public async Task<DoraMetricsSnapshot> GetMetricsAsync(int windowDays, string stage, DateTimeOffset? asOf, CancellationToken ct)
    {
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var deliveries = await scope.Model.GetDeliveriesAsync(1000, ct);
        var workItems = await scope.Model.GetWorkItemsAsync(ct);
        var workSummary = ComputeWorkSummary(workItems);
        return DoraMetricsCalculator.Calculate(deliveries, workSummary.ReworkRate, scope.Now, Math.Clamp(windowDays, 1, 90), stage);
    }

    public async Task<CausalExplanationDto> ExplainItemAsync(string query, DateTimeOffset? asOf, CancellationToken ct)
    {
        var normalized = query.Trim().TrimStart('#');
        var scope = await timeTravel.ResolveAsync(asOf, ct);
        var model = scope.Model;
        var workItems = await model.GetWorkItemsAsync(ct);
        var prs = await model.GetSourceChangesAsync(ct);
        var runs = await model.GetPipelineRunsAsync(300, ct);
        var releases = await model.GetReleasesAsync(300, ct);
        var deliveries = await model.GetDeliveriesAsync(500, ct);
        var workloads = await model.GetWorkloadsAsync(ct);
        var events = await store.QueryEventsAsync(null, scope.AsOf, null, null, null, null, 300, ct);

        WorkItemSnapshot? matchedIssue = null;
        if (int.TryParse(normalized, out var num))
        {
            matchedIssue = workItems.FirstOrDefault(w => w.Number == num);
        }
        matchedIssue ??= workItems.FirstOrDefault(w =>
            w.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.ReleaseCandidateVersion, normalized, StringComparison.OrdinalIgnoreCase));

        var matchedPrs = prs.Where(p =>
            (int.TryParse(normalized, out var n) && (p.Number == n || p.LinkedIssueNumber == n)) ||
            (matchedIssue is not null && p.LinkedIssueNumber == matchedIssue.Number) ||
            (!string.IsNullOrWhiteSpace(p.MergeCommitSha) && p.MergeCommitSha.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) ||
            p.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToList();

        var shas = matchedPrs
            .Select(p => p.MergeCommitSha)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalized.Length >= 7 && normalized.All(Uri.IsHexDigit))
        {
            shas.Add(normalized);
        }

        var matchedReleases = releases.Where(r =>
            r.Version.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            r.ReleaseId.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            (matchedIssue?.ReleaseCandidateVersion is not null && r.Version.Equals(matchedIssue.ReleaseCandidateVersion, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(r.GitCommitSha) && shas.Any(s => r.GitCommitSha.StartsWith(s, StringComparison.OrdinalIgnoreCase)))).ToList();

        var releaseVersions = matchedReleases.Select(r => r.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (matchedIssue?.ReleaseCandidateVersion is { } rcv)
        {
            releaseVersions.Add(rcv);
        }

        var matchedRuns = runs.Where(r =>
            (r.Version is not null && releaseVersions.Contains(r.Version)) ||
            shas.Any(s => r.HeadSha.StartsWith(s, StringComparison.OrdinalIgnoreCase)) ||
            r.RunId.ToString(CultureInfo.InvariantCulture).Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToList();

        var matchedDeliveries = deliveries.Where(d =>
            releaseVersions.Contains(d.ReleaseVersion) ||
            d.ReleaseVersion.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            d.TaskId.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            d.DeploymentId.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(d.GitCommitSha) && shas.Any(s => d.GitCommitSha.StartsWith(s, StringComparison.OrdinalIgnoreCase)))).ToList();

        var matchedWorkloads = workloads.Where(w =>
            releaseVersions.Contains(w.RunningVersion ?? "") ||
            releaseVersions.Contains(w.PinnedVersion ?? "") ||
            string.Equals(w.RunningVersion, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.ActiveRevisionName, normalized, StringComparison.OrdinalIgnoreCase)).ToList();

        var matchedEvents = events.Where(e =>
            e.Summary.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            e.SubjectId.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            (e.ChainId?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (matchedIssue is not null && e.Summary.Contains($"#{matchedIssue.Number}", StringComparison.OrdinalIgnoreCase))).Take(40).ToList();

        var found = matchedIssue is not null || matchedPrs.Count > 0 || matchedRuns.Count > 0 ||
                    matchedReleases.Count > 0 || matchedDeliveries.Count > 0 || matchedWorkloads.Count > 0 || matchedEvents.Count > 0;

        var summaryText = found
            ? $"Matched '{query}': {matchedPrs.Count} PR(s), {matchedRuns.Count} build run(s), {matchedReleases.Count} release(s), {matchedDeliveries.Count} deployment(s), {matchedWorkloads.Count} live workload(s)."
            : $"No work item, PR, commit, release, or deployment matched '{query}'.";

        return new CausalExplanationDto(
            query,
            found,
            summaryText,
            matchedIssue,
            matchedPrs,
            matchedRuns,
            matchedReleases,
            matchedDeliveries,
            matchedWorkloads,
            matchedEvents);
    }

    public static WorkBoardSummary ComputeWorkSummary(IReadOnlyList<WorkItemSnapshot> items)
    {
        var wip = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in BoardWipColumns)
        {
            wip[col] = items.Count(i => i.Column.Equals(col, StringComparison.OrdinalIgnoreCase));
        }

        var cycleTimes = items.Where(i => i.CycleTimeHours is > 0).Select(i => i.CycleTimeHours!.Value).OrderBy(x => x).ToList();
        var leadTimes = items.Where(i => i.LeadTimeHours is > 0).Select(i => i.LeadTimeHours!.Value).OrderBy(x => x).ToList();

        var tested = items.Count(i => i.FtFailures > 0 || i.MergedAt is not null || i.Column is "Review" or "Merged" or "TDD" or "UAT" or "Done");
        var reworked = items.Count(i => i.FtFailures > 0 || i.Reverts > 0);
        double? reworkRate = tested > 0 ? Math.Round((double)reworked / tested, 3) : null;

        var throughput = items
            .Where(i => i.WorkerKind == "bot" && i.Column == "Done" && !string.IsNullOrWhiteSpace(i.Agent))
            .GroupBy(i => i.Agent!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new WorkBoardSummary(
            TotalItems: items.Count,
            OpenItems: items.Count(i => i.State == "open"),
            BotItems: items.Count(i => i.WorkerKind == "bot"),
            TeamItems: items.Count(i => i.WorkerKind != "bot"),
            WipByColumn: wip,
            MedianCycleTimeHours: DoraMetricsCalculator.Percentile(cycleTimes, 0.5),
            P85CycleTimeHours: DoraMetricsCalculator.Percentile(cycleTimes, 0.85),
            MedianLeadTimeHours: DoraMetricsCalculator.Percentile(leadTimes, 0.5),
            ReworkRate: reworkRate,
            ThroughputByAgent: throughput);
    }

    private static (double? mtd, double? yesterday) ParseSystemCost(string? costJson)
    {
        if (string.IsNullOrWhiteSpace(costJson))
        {
            return (null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(costJson);
            if (doc.RootElement.TryGetProperty("system", out var sys))
            {
                double? mtd = sys.TryGetProperty("monthToDate", out var m) && m.TryGetDouble(out var mv) ? mv : null;
                double? yest = sys.TryGetProperty("yesterday", out var y) && y.TryGetDouble(out var yv) ? yv : null;
                return (mtd, yest);
            }
        }
        catch (JsonException)
        {
            return (null, null);
        }
        return (null, null);
    }
}
