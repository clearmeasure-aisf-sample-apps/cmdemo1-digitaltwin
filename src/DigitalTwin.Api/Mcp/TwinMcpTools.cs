using System.ComponentModel;
using System.Text.Json;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.Observation;
using ModelContextProtocol.Server;

namespace DigitalTwin.Api.Mcp;

[McpServerToolType]
public sealed class TwinMcpTools(TwinQueryService queries)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool(Name = "get_summary", Title = "Get Digital Twin Summary", ReadOnly = true, Idempotent = true)]
    [Description("Get the complete platform-level digital twin summary of cmdemo1: pipelines, deployment stages (tdd/uat/prod/pool), DORA metrics, active drift count, work board summary, ingestion health, GitHub quota, and durable intake queue (depth, oldest age, dead letters).")]
    public async Task<string> GetSummaryAsync(CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetSummaryAsync(ct), JsonOptions);

    [McpServerTool(Name = "query_events", Title = "Query Normalized Event Journal", ReadOnly = true, Idempotent = true)]
    [Description("Query the append-only normalized CloudEvents/CDEvents domain event journal across GitHub, Octopus Deploy, Azure, and live health probes.")]
    public async Task<string> QueryEventsAsync(
        [Description("Optional ISO-8601 UTC start timestamp filter")] DateTimeOffset? since = null,
        [Description("Optional source filter: GitHub, Octopus, Azure, GitOps, Probe, Twin")] EventSourceKind? source = null,
        [Description("Optional pipeline ID: app-ui, app-dashboard, system-gitops, measurement-scorecard, factory-agentic, twin-observer")] string? pipeline = null,
        [Description("Optional stage filter: tdd, uat, prod, pool")] string? stage = null,
        [Description("Optional CDEvents/twin event type")] string? eventType = null,
        [Description("Maximum number of events to return (1-500, default 50)")] int limit = 50,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.QueryEventsAsync(since, source, pipeline, stage, eventType, limit, ct), JsonOptions);

    [McpServerTool(Name = "get_environment", Title = "Get Environment Stage State", ReadOnly = true, Idempotent = true)]
    [Description("Get detailed runtime, hosting boundary, Container App revisions, health probes, recent deployments, and active drift for a deployment stage (tdd, uat, prod, or pool).")]
    public async Task<string> GetEnvironmentAsync(
        [Description("Deployment stage name: tdd, uat, prod, or pool")] string stage,
        CancellationToken ct = default)
    {
        var env = await queries.GetEnvironmentAsync(stage, ct);
        return env is null
            ? JsonSerializer.Serialize(new { error = $"Stage '{stage}' not found." }, JsonOptions)
            : JsonSerializer.Serialize(env, JsonOptions);
    }

    [McpServerTool(Name = "get_drift", Title = "Get Desired vs Observed Drift", ReadOnly = true, Idempotent = true)]
    [Description("List active (or historical resolved) desired-vs-observed drift findings comparing cmdemo1-system GitOps pins against live Azure and Octopus state.")]
    public async Task<string> GetDriftAsync(
        [Description("Include resolved drift findings (default false)")] bool includeResolved = false,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.GetDriftAsync(includeResolved, ct), JsonOptions);

    [McpServerTool(Name = "get_pipeline", Title = "Get Pipeline Details", ReadOnly = true, Idempotent = true)]
    [Description("Inspect a cmdemo1 pipeline (app-ui, app-dashboard, system-gitops, measurement-scorecard, factory-agentic, twin-observer) with pinned stage versions, latest CI run, open PRs, and active deliveries.")]
    public async Task<string> GetPipelineAsync(
        [Description("Pipeline ID or deployable/repo name")] string pipelineId,
        CancellationToken ct = default)
    {
        var summary = await queries.GetSummaryAsync(ct);
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
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await queries.ExplainItemAsync(query, ct), JsonOptions);
}
