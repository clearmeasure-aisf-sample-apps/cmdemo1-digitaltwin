using System.Globalization;
using System.Reflection;
using System.Text;
using DigitalTwin.Api.Auth;
using DigitalTwin.Api.Realtime;
using DigitalTwin.Core.Ports;
using DigitalTwin.Application.Intake;
using DigitalTwin.Application.Services;
using DigitalTwin.Application.Workers;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Observation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Api.Endpoints;

public static class TwinApiEndpoints
{
    public static void MapTwinEndpoints(this WebApplication app)
    {
        // Health checks for Container Apps & probes
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = PlatformCatalog.TwinServiceName, version = BuildVersion }));
        app.MapGet("/_healthcheck", () => Results.Text("Healthy\n"));

        // Public dashboard hydration + SignalR/SSE stream + public summary/graph
        app.MapGet("/api/dashboard/state", async (DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetDashboardStateAsync(asOf, ct)));

        app.MapGet("/api/twin/summary", async (DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetSummaryAsync(asOf, ct)))
            .AddEndpointFilter(TwinKeyAuth.ValidateOptionalTwinKey());

        app.MapGet("/api/twin/graph", async (DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetGraphAsync(asOf, ct)))
            .AddEndpointFilter(TwinKeyAuth.ValidateOptionalTwinKey());

        app.MapGet("/api/reconciliation/status", (ReconciliationService reconciliation) => Results.Ok(reconciliation.LastReport is { } r
            ? new { r.FinishedAt, r.DriftCount, r.ErrorCount, checks = r.Checks.Count, drifted = r.Checks.Where(c => c.Error is null && !c.InSync).Select(c => $"{c.Source} {c.Name} ({c.Scope})").ToList() }
            : null));

        // Public like the dashboard: what changed per environment between two instants (from the event journal).
        app.MapGet("/api/environments/compare", async (DateTimeOffset from, DateTimeOffset? to, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.CompareAsync(from, to, ct)));

        app.MapGet("/api/alerts/status", (AlertService alerts) =>
        {
            var snapshot = alerts.Snapshot();
            return Results.Ok(new { snapshot.SinkEnabled, snapshot.LastEvaluatedAt, active = snapshot.Active.Count, critical = snapshot.Active.Count(a => a.Severity == "critical"), titles = snapshot.Active.Select(a => a.Title).Take(5).ToList() });
        });

        app.MapGet("/api/intake/status", async (TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetIntakeQueueAsync(ct)));

        app.MapGet("/api/stream", async (HttpContext context, RealtimeEventPublisher publisher, CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var (subId, reader) = publisher.SubscribeSse();
            try
            {
                await context.Response.WriteAsync(": connected\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                await foreach (var msg in reader.ReadAllAsync(ct))
                {
                    await context.Response.WriteAsync($"event: {msg.EventName}\ndata: {msg.PayloadJson}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
            finally
            {
                publisher.UnsubscribeSse(subId);
            }
        });

        // Protected Query & Admin API (requires X-Twin-Key: TWIN_API_KEY)
        var protectedApi = app.MapGroup("/api")
            .AddEndpointFilter(TwinKeyAuth.RequireTwinKey());

        protectedApi.MapGet("/events", async (
            DateTimeOffset? since,
            DateTimeOffset? until,
            DateTimeOffset? asOf,
            EventSourceKind? source,
            string? pipeline,
            string? stage,
            string? eventType,
            int? limit,
            TwinQueryService queries,
            CancellationToken ct)
            => Results.Ok(await queries.QueryEventsAsync(since, until ?? asOf, source, pipeline, stage, eventType, limit ?? 100, ct)));

        protectedApi.MapGet("/pipelines", async (DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct) =>
        {
            var summary = await queries.GetSummaryAsync(asOf, ct);
            return Results.Ok(summary.Pipelines);
        });

        protectedApi.MapGet("/pipelines/{id}", async (string id, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct) =>
        {
            var summary = await queries.GetSummaryAsync(asOf, ct);
            var pipeline = summary.Pipelines.FirstOrDefault(p => p.Definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return pipeline is null ? Results.NotFound() : Results.Ok(pipeline);
        });

        protectedApi.MapGet("/environments/{env}", async (string env, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct) =>
        {
            var stage = await queries.GetEnvironmentAsync(env, asOf, ct);
            return stage is null ? Results.NotFound() : Results.Ok(stage);
        });

        protectedApi.MapGet("/drift", async (bool? includeResolved, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetDriftAsync(includeResolved ?? false, asOf, ct)));

        protectedApi.MapGet("/workitems", async (string? column, string? worker, int? limit, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetWorkItemsAsync(column, worker, limit ?? 100, asOf, ct)));

        protectedApi.MapGet("/deployments", async (string? stage, string? deployable, int? limit, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetDeploymentsAsync(stage, deployable, limit ?? 100, asOf, ct)));

        protectedApi.MapGet("/metrics", async (int? windowDays, string? stage, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetMetricsAsync(windowDays ?? 7, stage ?? PlatformCatalog.ProductionStage, asOf, ct)));

        protectedApi.MapGet("/explain", async (string q, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.ExplainItemAsync(q, asOf, ct)));

        protectedApi.MapGet("/change-chains", async (string? q, int? limit, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetChangeChainsAsync(q, limit ?? 20, asOf, ct)));

        protectedApi.MapGet("/alerts", (AlertService alerts) => Results.Ok(alerts.Snapshot()));

        protectedApi.MapPost("/admin/alerts/evaluate", async (AlertService alerts, CancellationToken ct) => Results.Ok(await alerts.EvaluateAsync(ct)));

        protectedApi.MapPost("/admin/alerts/test", async (AlertService alerts, CancellationToken ct) => Results.Ok(await alerts.SendTestAsync(ct)));

        protectedApi.MapGet("/dora", async (int? windowDays, string? deployable, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetChainDoraAsync(windowDays ?? 28, deployable, asOf, ct)));

        protectedApi.MapGet("/reconciliation", (ReconciliationService reconciliation)
            => Results.Ok(reconciliation.LastReport is { } report ? report : new { message = "No reconciliation has run yet; POST /api/admin/reconciliation/run." }));

        protectedApi.MapPost("/admin/reconciliation/run", async (ReconciliationService reconciliation, CancellationToken ct)
            => Results.Ok(await reconciliation.RunAsync(ct)));

        protectedApi.MapGet("/config-changes", async (DateTimeOffset? since, int? limit, DateTimeOffset? asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetConfigChangesAsync(since, limit ?? 50, asOf, ct)));

        protectedApi.MapGet("/state-at", async (DateTimeOffset asOf, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetStateAtAsync(asOf, ct)));

        // Full rebuild of the journal and every read model from the raw intake store.
        // Refuses (Aborted) when the raw store cannot reproduce every live row, unless force=true.
        protectedApi.MapPost("/admin/rebuild", async (bool? wait, bool? force, IntakeRebuildService rebuild) =>
        {
            var running = rebuild.StartOrJoin(force ?? false);
            return wait == false ? Results.Accepted("/api/admin/rebuild/status", rebuild.GetStatus()) : Results.Ok(await running);
        });

        // Raw intake retention: archive whole days older than the window (default Twin:Retention:Days), then prune SQL.
        protectedApi.MapPost("/admin/retention/run", async (double? olderThanDays, bool? wait, RawIntakeRetentionService retention) =>
        {
            var running = retention.StartOrJoin(olderThanDays is null ? null : Math.Max(0, olderThanDays.Value));
            return wait == false ? Results.Accepted("/api/admin/storage", new { running = retention.Running }) : Results.Ok(await running);
        });

        protectedApi.MapGet("/admin/storage", async (RawIntakeRetentionService retention, IRawIntakeArchive archive, IRawIntakeStore rawStore, ITwinStore store, CancellationToken ct) =>
        {
            var days = await archive.ListDaysAsync(ct);
            return Results.Ok(new
            {
                database = await store.GetDatabaseUsageAsync(ct),
                rawIntake = await rawStore.GetStatsAsync(ct),
                archive = new { enabled = archive.IsEnabled, location = archive.Location, days = days.Count, oldestDay = days.Count > 0 ? days[0] : (DateOnly?)null, newestDay = days.Count > 0 ? days[^1] : (DateOnly?)null },
                retention = new { running = retention.Running, lastReport = retention.LastReport, lastError = retention.LastError }
            });
        });

        protectedApi.MapGet("/admin/rebuild/preflight", async (RebuildPreflight preflight, CancellationToken ct)
            => Results.Ok(await preflight.RunAsync(ct)));

        protectedApi.MapPost("/admin/rebuild/cancel", (IntakeRebuildService rebuild)
            => Results.Ok(new { cancelled = rebuild.Cancel() }));

        protectedApi.MapGet("/admin/rebuild/status", (IntakeRebuildService rebuild) => Results.Ok(rebuild.GetStatus()));

        // Re-poll every source now (publishes state observations through the intake).
        protectedApi.MapPost("/admin/backfill", async (IBackfillCoordinator backfill, CancellationToken ct)
            => Results.Ok(await backfill.RebuildAllAsync("manual-admin-backfill", ct)));

        protectedApi.MapGet("/intake", async (TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetIntakeQueueAsync(ct)));

        protectedApi.MapGet("/admin/deadletters", async (int? limit, IDeadLetterAdmin deadLetters, CancellationToken ct)
            => Results.Ok(await deadLetters.ListAsync(limit ?? 100, ct)));

        protectedApi.MapPost("/admin/deadletters/replay", async (
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeadLetterReplayRequest? request,
            IDeadLetterAdmin deadLetters,
            CancellationToken ct) =>
        {
            var replayed = await deadLetters.ReplayAsync(request?.Ids, ct);
            return Results.Ok(new { replayed });
        });

        protectedApi.MapPost("/admin/rebuild-from-raw", async (IntakeRebuildService rebuild) => Results.Ok(await rebuild.StartOrJoin()));

        protectedApi.MapPost("/admin/register-hooks", async (
            string? publicBaseUrl,
            IOptions<TwinHostOptions> options,
            IGitHubSyncService gitHub,
            IOctopusSyncService octopus,
            IAzureSyncService azure,
            CancellationToken ct) =>
        {
            var baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? options.Value.TwinPublicUrl : publicBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Results.BadRequest(new { error = "TWIN_PUBLIC_URL or publicBaseUrl query parameter required." });
            }

            var ghHooks = await gitHub.EnsureWebhooksRegisteredAsync(baseUrl, ct);
            var octoHook = await octopus.EnsureSubscriptionRegisteredAsync(baseUrl, ct);
            var azHook = await azure.TryEnsureEventGridSubscriptionsAsync(baseUrl, ct);
            return Results.Ok(new { baseUrl, github = ghHooks, octopus = octoHook, azure = azHook });
        });

        app.MapGet("/llms.txt", (HttpContext ctx) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Text(BuildLlmsTxt(baseUrl), "text/plain; charset=utf-8");
        });
    }

    private static readonly string BuildVersion =
        typeof(TwinApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private static string BuildLlmsTxt(string baseUrl)
    {
        return string.Create(CultureInfo.InvariantCulture, $$"""
            # cmdemo1-digitaltwin — Platform DevOps Digital Twin

            Platform: {{PlatformCatalog.PlatformName}}
            Observed System: {{PlatformCatalog.SystemSlug}} ({{PlatformCatalog.SystemName}})
            GitOps Repository: https://github.com/{{PlatformCatalog.GitHubOrg}}/{{PlatformCatalog.SystemRepository}}
            Octopus Space: {{PlatformCatalog.OctopusUrl}}/app#/{{PlatformCatalog.OctopusSpaceId}} ({{PlatformCatalog.OctopusSpaceSlug}})

            ## Architecture Intent
            This digital twin sits ONE LEVEL ABOVE cmdemo1 (it observes cmdemo1 and is not a cmdemo1 deployable).
            Hierarchy: Platform > System (cmdemo1) > Pipelines > DeploymentStages (tdd, uat, prod, pool) > HostingBoundaries > Workloads/Revisions/AzureResources.
            Every webhook and poller observation is durably captured in a raw intake table plus a Wolverine queue (transactional outbox) before it is acknowledged, then applied idempotently by a separate processor.

            ## Authentication
            - Dashboard (`/`, `/api/dashboard/state`, `/api/twin/summary`, `/api/twin/graph`, `/llms.txt`) is public read.
            - Protected Query API and MCP endpoints require header `X-Twin-Key: <TWIN_API_KEY>` (or `Authorization: Bearer <TWIN_API_KEY>`).

            ## REST Endpoints
            - GET {{baseUrl}}/api/twin/summary — System summary, pipelines, stages, DORA metrics, active drift, ingestion cursors
            - GET {{baseUrl}}/api/twin/graph — Typed entity & relationship graph (Platform -> System -> Pipelines -> Stages -> Workloads)
            - GET {{baseUrl}}/api/events?since=&until=&source=&pipeline=&stage=&limit= — Append-only normalized CloudEvents/CDEvents journal [X-Twin-Key]
            - GET {{baseUrl}}/api/pipelines — Pipeline definitions, stage versions, latest CI runs, open PRs [X-Twin-Key]
            - GET {{baseUrl}}/api/environments/{env} — Stage details (tdd|uat|prod|pool), workloads, revisions, probes, drift [X-Twin-Key]
            - GET {{baseUrl}}/api/drift?includeResolved=false — Desired vs observed drift findings [X-Twin-Key]
            - GET {{baseUrl}}/api/workitems?column=&worker=&limit= — GitHub Project 679 work items and bot/team flow [X-Twin-Key]
            - GET {{baseUrl}}/api/deployments?stage=&deployable=&limit= — Octopus releases, deployments, sign-offs, guided failures [X-Twin-Key]
            - GET {{baseUrl}}/api/metrics?windowDays=7&stage=prod — DORA 5-metric delivery scorecard [X-Twin-Key]
            - GET {{baseUrl}}/api/explain?q=<issue|pr|version|sha|task> — Causal trace across work -> PR -> build -> release -> deploy -> runtime [X-Twin-Key]
            - GET {{baseUrl}}/api/change-chains?q=<issue|pr|sha|version|release>&limit=&asOf= — Change chains: work item -> PR -> build -> RC -> Octopus release -> tdd -> uat -> prod with stage timings and failures [X-Twin-Key]
            - GET {{baseUrl}}/api/environments/compare?from=&to= — Before/after: versions and revisions per environment, Azure resources added/removed, drift and board changes between two instants (public)
            - GET {{baseUrl}}/api/alerts — Active alerts (deploy failures, stalled changes, dead letters, reconciliation drift) and recently sent ones [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/alerts/test — Send a test alert to the configured webhook (Twin:Alerts:WebhookUrl; off when empty) [X-Twin-Key]
            - GET {{baseUrl}}/api/dora?windowDays=28&deployable=&asOf= — DORA keys from change chains: deployment frequency, lead time, change failure rate, time to restore, with performance bands [X-Twin-Key]
            - GET {{baseUrl}}/api/reconciliation — Latest daily comparison of the twin against GitHub and Octopus: drifted checks with what the twin is missing [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/reconciliation/run — Reconcile now (drifted repositories and Octopus are refreshed) [X-Twin-Key]
            - GET {{baseUrl}}/api/config-changes?since=&limit=&asOf= — Octopus deployment process, variable, project, trigger and runbook changes [X-Twin-Key]
            - GET {{baseUrl}}/api/state-at?asOf=<ISO-8601> — Time travel: versions per environment, Azure resources, board columns and drift at that instant [X-Twin-Key]
            - GET {{baseUrl}}/api/intake — Durable intake queue: depth, oldest pending age, dead letters, transport [X-Twin-Key]
            - GET {{baseUrl}}/api/admin/deadletters — Dead-lettered intake messages [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/deadletters/replay — Replay dead letters (all, or ids in the JSON body) [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/rebuild[?force=true&wait=false] — Rebuild the journal and ALL read models from the raw history into shadow tables without pausing intake; swaps them in atomically only when the read models match live (or force); reports fingerprints [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/retention/run[?olderThanDays=&wait=false] — Archive whole days of processed raw events to Blob Storage, then prune them from SQL [X-Twin-Key]
            - GET {{baseUrl}}/api/admin/storage — Database usage, raw intake counts, archive days, last retention run [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/rebuild/cancel — Stop a running rebuild; its shadow tables are discarded and live state is untouched [X-Twin-Key]
            - GET {{baseUrl}}/api/admin/rebuild/preflight — What a rebuild would produce versus the live read models, per kind [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/backfill — Re-poll GitHub, Octopus, Azure and probes now [X-Twin-Key]

            ## Time Travel
            Every read endpoint above (summary, graph, dashboard state, pipelines, environments, drift, workitems, deployments, metrics, explain, change-chains) accepts `asOf=<ISO-8601>`
            and answers from the state reconstructed from the event journal at that instant. State history starts at the first state observation.

            ## MCP Server (Streamable HTTP)
            Endpoint: {{baseUrl}}/mcp (requires `X-Twin-Key` header)
            Tools: get_summary, get_alerts, get_dora_metrics, get_change_chain, get_config_changes, get_reconciliation, get_state_at, query_events, get_environment, get_drift, get_pipeline, explain_item, get_intake_queue (read tools accept `asOf`)
            """);
    }
}

public sealed record DeadLetterReplayRequest(Guid[]? Ids);
