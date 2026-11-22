using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.Observation;
using ModelContextProtocol.Server;

namespace DigitalTwin.Api.Mcp;

[McpServerToolType]
public sealed class TwinMcpTools(TwinQueryService queries, ReconciliationService reconciliation)
{
    private const string AsOfDescription = "Optional ISO-8601 UTC timestamp: answer from the twin's state as it was at that instant (time travel). Omit for live state.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "get_summary", Title = "Get Digital Twin Summary", ReadOnly = true, Idempotent = true)]
    [Description("Get the complete platform-level digital twin summary of cmdemo1: pipelines, deployment stages (tdd/uat/prod/pool), DORA metrics, active drift count, work board summary, ingestion health, GitHub quota, and durable intake queue (depth, oldest age, dead letters).")]
    public async Task<string> GetSummaryAsync(
        [Description(AsOfDescription)] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetSummaryAsync(asOf, ct), JsonOptions);

    [McpServerTool(Name = "get_change_chain", Title = "Get Change Chain", ReadOnly = true, Idempotent = true)]
    [Description("Trace a change end to end: work item -> branch/PR -> CI build -> release candidate -> Octopus release -> tdd -> uat -> prod, with each stage's status, timings, attempts and failures, plus the journal events of the best match. Omit the query for the most recent changes.")]
    public async Task<string> GetChangeChainAsync(
        [Description("Issue number, PR number, commit SHA (prefix), release version (e.g. 2.4.2664), Octopus release/deployment/task id, or chain id. Omit for recent chains.")] string? query = null,
        [Description("Maximum number of chains to return (1-100, default 5)")] int limit = 5,
        [Description(AsOfDescription)] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetChangeChainsAsync(query, limit, asOf, ct), JsonOptions);

    [McpServerTool(Name = "get_config_changes", Title = "Get Octopus Configuration Changes", ReadOnly = true, Idempotent = true)]
    [Description("List Octopus configuration changes in the cmdemo1 space, newest first: deployment process edits, variable set changes, project settings, triggers and runbooks, with who made them. Useful when a deployment starts failing.")]
    public async Task<string> GetConfigChangesAsync(
        [Description("Look back this many hours (default 168 = 7 days)")] int sinceHours = 168,
        [Description("Maximum number of changes (1-500, default 50)")] int limit = 50,
        [Description(AsOfDescription)] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetConfigChangesAsync((asOf ?? DateTimeOffset.UtcNow).AddHours(-Math.Max(1, sinceHours)), limit, asOf, ct), JsonOptions);

    [McpServerTool(Name = "get_reconciliation", Title = "Get Twin Accuracy Reconciliation", ReadOnly = true, Idempotent = true)]
    [Description("How accurate the twin is: the latest comparison against GitHub (open PRs, recently active issues per repository) and Octopus (deployed version per deployable and environment, recent releases), with what the twin is missing or has extra.")]
    public async Task<string> GetReconciliationAsync(
        [Description("Run a fresh reconciliation now instead of returning the latest daily report (default false)")] bool runNow = false,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(runNow || reconciliation.LastReport is null ? await reconciliation.RunAsync(ct) : reconciliation.LastReport, JsonOptions);

    [McpServerTool(Name = "get_state_at", Title = "Get Point-in-Time State", ReadOnly = true, Idempotent = true)]
    [Description("Time travel: what version was deployed to each environment (tdd/uat/prod), which Azure resources existed, the work board columns, and active drift at a given instant, reconstructed from the event journal.")]
    public async Task<string> GetStateAtAsync(
        [Description("ISO-8601 UTC timestamp to reconstruct state at, e.g. 2026-11-21T12:00:00Z")] DateTimeOffset asOf,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetStateAtAsync(asOf, ct), JsonOptions);

    [McpServerTool(Name = "query_events", Title = "Query Normalized Event Journal", ReadOnly = true, Idempotent = true)]
    [Description("Query the append-only normalized CloudEvents/CDEvents domain event journal across GitHub, Octopus Deploy, Azure, and live health probes.")]
    public async Task<string> QueryEventsAsync(
        [Description("Optional ISO-8601 UTC start timestamp filter")] DateTimeOffset? since = null,
        [Description("Optional source filter: GitHub, Octopus, Azure, GitOps, Probe, Twin")] EventSourceKind? source = null,
        [Description("Optional pipeline ID: app-ui, app-dashboard, system-gitops, measurement-scorecard, factory-agentic, twin-observer")] string? pipeline = null,
        [Description("Optional stage filter: tdd, uat, prod, pool")] string? stage = null,
        [Description("Optional CDEvents/twin event type")] string? eventType = null,
        [Description("Maximum number of events to return (1-500, default 50)")] int limit = 50,
        [Description("Optional ISO-8601 UTC end timestamp: only events that occurred at or before it")] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.QueryEventsAsync(since, asOf, source, pipeline, stage, eventType, limit, ct), JsonOptions);

    [McpServerTool(Name = "get_environment", Title = "Get Environment Stage State", ReadOnly = true, Idempotent = true)]
    [Description("Get detailed runtime, hosting boundary, Container App revisions, health probes, recent deployments, and active drift for a deployment stage (tdd, uat, prod, or pool).")]
    public async Task<string> GetEnvironmentAsync(
        [Description("Deployment stage name: tdd, uat, prod, or pool")] string stage,
        [Description(AsOfDescription)] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
    {
        var env = await queries.GetEnvironmentAsync(stage, asOf, ct);
        return env is null
            ? JsonSerializer.Serialize(new { error = $"Stage '{stage}' not found." }, JsonOptions)
            : JsonSerializer.Serialize(env, JsonOptions);
    }

    [McpServerTool(Name = "get_drift", Title = "Get Desired vs Observed Drift", ReadOnly = true, Idempotent = true)]
    [Description("List active (or historical resolved) desired-vs-observed drift findings comparing cmdemo1-system GitOps pins against live Azure and Octopus state.")]
    public async Task<string> GetDriftAsync(
        [Description("Include resolved drift findings (default false)")] bool includeResolved = false,
        [Description(AsOfDescription)] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetDriftAsync(includeResolved, asOf, ct), JsonOptions);

    [McpServerTool(Name = "get_pipeline", Title = "Get Pipeline Details", ReadOnly = true, Idempotent = true)]
    [Description("Inspect a cmdemo1 pipeline (app-ui, app-dashboard, system-gitops, measurement-scorecard, factory-agentic, twin-observer) with pinned stage versions, latest CI run, open PRs, and active deliveries.")]
    public async Task<string> GetPipelineAsync(
        [Description("Pipeline ID or deployable/repo name")] string pipelineId,
        [Description(AsOfDescription)] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
    {
        var summary = await queries.GetSummaryAsync(asOf, ct);
        var match = summary.Pipelines.FirstOrDefault(p =>
            p.Definition.Id.Equals(pipelineId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Definition.DeployableName, pipelineId, StringComparison.OrdinalIgnoreCase) ||
            p.Definition.Repository.Equals(pipelineId, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? JsonSerializer.Serialize(new { error = $"Pipeline '{pipelineId}' not found." }, JsonOptions)
            : JsonSerializer.Serialize(match, JsonOptions);
    }

    [McpServerTool(Name = "get_intake_queue", Title = "Get Durable Intake Queue Status", ReadOnly = true, Idempotent = true)]
    [Description("Get the durable intake queue status: transport, queue depth (captured but not yet applied), oldest pending event age, dead-letter count, captured/processed totals.")]
    public async Task<string> GetIntakeQueueAsync(CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetIntakeQueueAsync(ct), JsonOptions);

    [McpServerTool(Name = "explain_item", Title = "Explain Item Causal Chain", ReadOnly = true, Idempotent = true)]
    [Description("Trace an issue number, PR number, release version (e.g. 2.4.2664), commit SHA, or Octopus task/deployment ID across Work -> PR -> Build -> Release -> Deployments -> Azure Runtime.")]
    public async Task<string> ExplainItemAsync(
        [Description("Issue number, PR number, version string, commit SHA prefix, or Octopus task/deployment ID")] string query,
        [Description(AsOfDescription)] DateTimeOffset? asOf = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.ExplainItemAsync(query, asOf, ct), JsonOptions);
}
