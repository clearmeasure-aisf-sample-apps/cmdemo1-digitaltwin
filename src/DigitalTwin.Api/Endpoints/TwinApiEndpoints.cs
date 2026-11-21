using System.Globalization;
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
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "cmdemo1-digitaltwin" }));
        app.MapGet("/_healthcheck", () => Results.Text("Healthy\n"));

        // Public dashboard hydration + SignalR/SSE stream + public summary/graph
        app.MapGet("/api/dashboard/state", async (TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetDashboardStateAsync(ct)));

        app.MapGet("/api/twin/summary", async (TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetSummaryAsync(ct)))
            .AddEndpointFilter(TwinKeyAuth.ValidateOptionalTwinKey());

        app.MapGet("/api/twin/graph", async (TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetGraphAsync(ct)))
            .AddEndpointFilter(TwinKeyAuth.ValidateOptionalTwinKey());

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
            EventSourceKind? source,
            string? pipeline,
            string? stage,
            string? eventType,
            int? limit,
            TwinQueryService queries,
            CancellationToken ct)
            => Results.Ok(await queries.QueryEventsAsync(since, source, pipeline, stage, eventType, limit ?? 100, ct)));

        protectedApi.MapGet("/pipelines", async (TwinQueryService queries, CancellationToken ct) =>
        {
            var summary = await queries.GetSummaryAsync(ct);
            return Results.Ok(summary.Pipelines);
        });

        protectedApi.MapGet("/pipelines/{id}", async (string id, TwinQueryService queries, CancellationToken ct) =>
        {
            var summary = await queries.GetSummaryAsync(ct);
            var pipeline = summary.Pipelines.FirstOrDefault(p => p.Definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return pipeline is null ? Results.NotFound() : Results.Ok(pipeline);
        });

        protectedApi.MapGet("/environments/{env}", async (string env, TwinQueryService queries, CancellationToken ct) =>
        {
            var stage = await queries.GetEnvironmentAsync(env, ct);
            return stage is null ? Results.NotFound() : Results.Ok(stage);
        });

        protectedApi.MapGet("/drift", async (bool? includeResolved, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetDriftAsync(includeResolved ?? false, ct)));

        protectedApi.MapGet("/workitems", async (string? column, string? worker, int? limit, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetWorkItemsAsync(column, worker, limit ?? 100, ct)));

        protectedApi.MapGet("/deployments", async (string? stage, string? deployable, int? limit, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetDeploymentsAsync(stage, deployable, limit ?? 100, ct)));

        protectedApi.MapGet("/metrics", async (int? windowDays, string? stage, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.GetMetricsAsync(windowDays ?? 7, stage ?? "prod", ct)));

        protectedApi.MapGet("/explain", async (string q, TwinQueryService queries, CancellationToken ct)
            => Results.Ok(await queries.ExplainItemAsync(q, ct)));

        protectedApi.MapPost("/admin/rebuild", async (IBackfillCoordinator backfill, CancellationToken ct)
            => Results.Ok(await backfill.RebuildAllAsync("manual-admin-rebuild", ct)));

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

        protectedApi.MapPost("/admin/rebuild-from-raw", async (IntakeRebuildService rebuild, CancellationToken ct)
            => Results.Ok(await rebuild.RebuildFromRawAsync(ct)));

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

    private static string BuildLlmsTxt(string baseUrl)
    {
        return string.Create(CultureInfo.InvariantCulture, $$"""
            # cmdemo1-digitaltwin — Platform DevOps Digital Twin

            Platform: {{Cmdemo1PlatformCatalog.PlatformName}}
            Observed System: {{Cmdemo1PlatformCatalog.SystemSlug}} ({{Cmdemo1PlatformCatalog.SystemName}})
            GitOps Repository: https://github.com/{{Cmdemo1PlatformCatalog.GitHubOrg}}/{{Cmdemo1PlatformCatalog.SystemRepository}}
            Octopus Space: {{Cmdemo1PlatformCatalog.OctopusUrl}}/app#/{{Cmdemo1PlatformCatalog.OctopusSpaceId}} ({{Cmdemo1PlatformCatalog.OctopusSpaceSlug}})

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
            - GET {{baseUrl}}/api/events?since=&source=&pipeline=&stage=&limit= — Append-only normalized CloudEvents/CDEvents journal [X-Twin-Key]
            - GET {{baseUrl}}/api/pipelines — Pipeline definitions, stage versions, latest CI runs, open PRs [X-Twin-Key]
            - GET {{baseUrl}}/api/environments/{env} — Stage details (tdd|uat|prod|pool), workloads, revisions, probes, drift [X-Twin-Key]
            - GET {{baseUrl}}/api/drift?includeResolved=false — Desired vs observed drift findings [X-Twin-Key]
            - GET {{baseUrl}}/api/workitems?column=&worker=&limit= — GitHub Project 679 work items and bot/team flow [X-Twin-Key]
            - GET {{baseUrl}}/api/deployments?stage=&deployable=&limit= — Octopus releases, deployments, sign-offs, guided failures [X-Twin-Key]
            - GET {{baseUrl}}/api/metrics?windowDays=7&stage=prod — DORA 5-metric delivery scorecard [X-Twin-Key]
            - GET {{baseUrl}}/api/explain?q=<issue|pr|version|sha|task> — Causal trace across work -> PR -> build -> release -> deploy -> runtime [X-Twin-Key]
            - GET {{baseUrl}}/api/intake — Durable intake queue: depth, oldest pending age, dead letters, transport [X-Twin-Key]
            - GET {{baseUrl}}/api/admin/deadletters — Dead-lettered intake messages [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/deadletters/replay — Replay dead letters (all, or ids in the JSON body) [X-Twin-Key]
            - POST {{baseUrl}}/api/admin/rebuild-from-raw — Rebuild the event journal from the raw intake table [X-Twin-Key]

            ## MCP Server (Streamable HTTP)
            Endpoint: {{baseUrl}}/mcp (requires `X-Twin-Key` header)
            Tools: get_summary, query_events, get_environment, get_drift, get_pipeline, explain_item, get_intake_queue
            """);
    }
}

public sealed record DeadLetterReplayRequest(Guid[]? Ids);
