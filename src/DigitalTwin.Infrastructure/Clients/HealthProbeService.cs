using System.Diagnostics;
using System.Text.Json;
using DigitalTwin.Application.Abstractions;
using DigitalTwin.Application.Services;
using DigitalTwin.Domain.Observation;
using DigitalTwin.Domain.ReliabilityAndDrift;
using Microsoft.Extensions.Logging;

namespace DigitalTwin.Infrastructure.Clients;

public sealed partial class HealthProbeService(
    IHttpClientFactory httpClientFactory,
    ITwinStore store,
    EventIngestionService ingestion,
    TimeProvider timeProvider,
    ILogger<HealthProbeService> logger) : IHealthProbeService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Health probe cycle failed")]
    private partial void LogProbeFailed(Exception ex);

    public async Task ProbeAllAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = new IngestionCursor
        {
            SourceKey = "probes",
            LastAttemptAt = now,
            Status = "running"
        };

        try
        {
            var targets = new List<(string Stage, string Deployable, string Hosting, string BaseUrl, string HealthPath)>();

            var workloads = await store.GetWorkloadsAsync(ct);
            foreach (var w in workloads.Where(w => !string.IsNullOrWhiteSpace(w.Fqdn)))
            {
                var path = w.HostingType.Equals("staticwebapp", StringComparison.OrdinalIgnoreCase) ? "/" : "/_healthcheck";
                targets.Add((w.StageName, w.DeployableName, w.HostingType, $"https://{w.Fqdn}", path));
            }

            // Fallback / supplemental addresses from status/containers.json
            var desired = await store.GetDesiredConfigAsync(ct);
            if (!string.IsNullOrWhiteSpace(desired?.NodesJson))
            {
                using var doc = JsonDocument.Parse(desired.NodesJson);
                if (doc.RootElement.TryGetProperty("environments", out var envs))
                {
                    foreach (var envProp in envs.EnumerateObject())
                    {
                        if (envProp.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var appProp in envProp.Value.EnumerateObject())
                        {
                            var url = appProp.Value.GetString();
                            if (string.IsNullOrWhiteSpace(url)) continue;
                            if (!targets.Any(t => t.Stage.Equals(envProp.Name, StringComparison.OrdinalIgnoreCase) &&
                                                  t.Deployable.Equals(appProp.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                var hosting = appProp.Name.Equals("dashboard", StringComparison.OrdinalIgnoreCase) ? "staticwebapp" : "containerapp";
                                var path = hosting == "staticwebapp" ? "/" : "/_healthcheck";
                                targets.Add((envProp.Name, appProp.Name, hosting, url.TrimEnd('/'), path));
                            }
                        }
                    }
                }
            }

            using var client = httpClientFactory.CreateClient("health-probe");
            client.Timeout = TimeSpan.FromSeconds(12);

            var probes = new List<HealthProbeSnapshot>();
            var swaVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (stage, deployable, hosting, baseUrl, healthPath) in targets)
            {
                ct.ThrowIfCancellationRequested();
                var probe = await ProbeSingleAsync(client, stage, deployable, hosting, baseUrl, healthPath, now, ct);
                probes.Add(probe);

                if (hosting == "staticwebapp" && !string.IsNullOrWhiteSpace(probe.ReportedVersion))
                {
                    swaVersions[$"{stage}:{deployable}"] = probe.ReportedVersion;
                }

                if (!probe.IsHealthy)
                {
                    await ingestion.IngestAsync(new ObservedEvent
                    {
                        EventId = $"probe-fail-{stage}-{deployable}-{now:yyyyMMddHHmm}",
                        IdempotencyKey = $"probe-fail:{stage}:{deployable}:{now:yyyyMMddHHmm}",
                        Source = EventSourceKind.Probe,
                        SourceEventId = $"{stage}:{deployable}:{now:O}",
                        EventType = "dev.cdevents.incident.detected",
                        SubjectType = "service",
                        SubjectId = $"{deployable}@{stage}",
                        Predicate = "detected",
                        OccurredAt = now,
                        IngestedAt = now,
                        Channel = IngestionChannel.Probe,
                        PipelineId = deployable == "dashboard" ? "app-dashboard" : "app-ui",
                        StageName = stage,
                        DeployableName = deployable,
                        Actor = "twin-health-prober",
                        Outcome = EventOutcome.Failure,
                        Summary = $"{deployable} in {stage} failed health probe ({probe.HttpStatusCode}): {probe.ErrorMessage ?? "unhealthy"}",
                        SourceUrl = $"{baseUrl}{healthPath}"
                    }, ct);
                }
            }

            await store.UpsertHealthProbesAsync(probes, ct);

            // Enrich SWA workloads with live build-facts.json reported version
            if (swaVersions.Count > 0 && workloads.Count > 0)
            {
                var updated = workloads.Select(w =>
                {
                    if (swaVersions.TryGetValue(w.Id, out var ver))
                    {
                        w.RunningVersion = ver;
                    }
                    return w;
                }).ToList();
                await store.ReplaceWorkloadsAsync(updated, ct);
            }

            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.Status = "ok";
            cursor.ItemsProcessed = probes.Count;
            await store.UpsertCursorAsync(cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProbeFailed(ex);
            cursor.Status = "error";
            cursor.LastError = ex.Message;
            await store.UpsertCursorAsync(cursor, ct);
        }
    }

    private static async Task<HealthProbeSnapshot> ProbeSingleAsync(
        HttpClient client,
        string stage,
        string deployable,
        string hosting,
        string baseUrl,
        string healthPath,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var statusCode = 0;
        var isHealthy = false;
        string? dbVer = null;
        string? reportedVer = null;
        string? reportedCommit = null;
        string? buildJson = null;
        string? errMsg = null;

        try
        {
            using var resp = await client.GetAsync($"{baseUrl}{healthPath}", ct);
            statusCode = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct);
            isHealthy = resp.IsSuccessStatusCode;

            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("databaseVersion:", StringComparison.OrdinalIgnoreCase))
                {
                    dbVer = line["databaseVersion:".Length..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errMsg = ex.Message;
        }
        sw.Stop();

        var buildPath = hosting == "staticwebapp" ? "/build-facts.json" : "/_build";
        try
        {
            using var bResp = await client.GetAsync($"{baseUrl}{buildPath}", ct);
            if (bResp.IsSuccessStatusCode)
            {
                buildJson = await bResp.Content.ReadAsStringAsync(ct);
                using var bDoc = JsonDocument.Parse(buildJson);
                if (bDoc.RootElement.TryGetProperty("version", out var v)) reportedVer = v.GetString();
                if (bDoc.RootElement.TryGetProperty("commit", out var c)) reportedCommit = c.GetString();
            }
        }
        catch
        {
            // build facts optional if placeholder or cold
        }

        return new HealthProbeSnapshot
        {
            TargetKey = $"{stage}:{deployable}",
            StageName = stage,
            DeployableName = deployable,
            Hosting = hosting,
            Url = baseUrl,
            HealthPath = healthPath,
            HttpStatusCode = statusCode,
            IsHealthy = isHealthy,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            ReportedVersion = reportedVer,
            ReportedCommit = reportedCommit,
            DatabaseVersion = dbVer,
            BuildFactsJson = buildJson,
            ErrorMessage = errMsg,
            CheckedAt = now
        };
    }
}
