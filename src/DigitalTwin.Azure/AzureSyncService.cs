using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Normalization;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.RuntimeAndPlatform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Azure;

public sealed partial class AzureSyncService(
    IHttpClientFactory httpClientFactory,
    ITwinStore store,
    StateObservationPublisher state,
    ObservedEventIntake intake,
    IOptions<AzureOptions> options,
    TimeProvider timeProvider,
    ILogger<AzureSyncService> logger) : IAzureSyncService
{
    private static readonly string[] ArmResourceGroups =
    [
        Cmdemo1PlatformCatalog.NonprodResourceGroup,
        Cmdemo1PlatformCatalog.ProdResourceGroup
    ];

    private static readonly string[] EventGridIncludedEventTypes =
    [
        "Microsoft.Resources.ResourceWriteSuccess",
        "Microsoft.Resources.ResourceWriteFailure",
        "Microsoft.Resources.ResourceWriteCancel",
        "Microsoft.Resources.ResourceDeleteSuccess",
        "Microsoft.Resources.ResourceDeleteFailure",
        "Microsoft.Resources.ResourceDeleteCancel",
        "Microsoft.Resources.ResourceActionSuccess",
        "Microsoft.Resources.ResourceActionFailure",
        "Microsoft.Resources.ResourceActionCancel"
    ];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Azure sync failed")]
    private partial void LogAzureSyncFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event Grid registration probe failed")]
    private partial void LogEventGridProbeFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACR tag query skipped")]
    private partial void LogAcrSkipped(Exception ex);

    public async Task SyncAsync(bool fullBackfill, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = new IngestionCursor
        {
            SourceKey = "azure",
            LastAttemptAt = now,
            Status = "running"
        };

        try
        {
            if (string.IsNullOrWhiteSpace(options.Value.ClientId) ||
                string.IsNullOrWhiteSpace(options.Value.ClientSecret) ||
                string.IsNullOrWhiteSpace(options.Value.TenantId))
            {
                cursor.Status = "skipped";
                cursor.LastError = "AZURE_CLIENT_ID / SECRET / TENANT_ID not configured";
                await store.UpsertCursorAsync(cursor, ct);
                return;
            }

            var token = await GetArmAccessTokenAsync(ct);
            using var arm = CreateArmClient(token);
            var subId = options.Value.SubscriptionId;

            // 1. Resource Health per RG
            var healthById = new Dictionary<string, (string Status, string? Summary)>(StringComparer.OrdinalIgnoreCase);
            foreach (var rg in ArmResourceGroups)
            {
                await FetchResourceHealthAsync(arm, subId, rg, healthById, ct);
            }

            // 2. Resource Graph query for all resources in both RGs
            var resources = await QueryResourcesAsync(arm, subId, healthById, now, ct);
            await state.PublishSetAsync(StateKinds.AzureResource, "azure", resources, EventSourceKind.Azure, ct);

            // 3. Deployment Stacks (stack-cmdemo1-tdd, stack-cmdemo1-uat, stack-cmdemo1-prod)
            var stacksByStage = await FetchStacksAsync(arm, subId, ct);

            // 4. Container Apps + Revisions & Static Web Apps -> WorkloadRevisionSnapshots
            var desired = await store.GetDesiredConfigAsync(ct);
            var pinned = DigitalTwin.Core.ReliabilityAndDrift.DriftEvaluator.ParsePinnedVersions(desired?.PinnedVersionsJson);
            var workloads = new List<WorkloadRevisionSnapshot>();
            var newEvents = new List<ObservedEvent>();

            var previousWorkloads = (await store.GetWorkloadsAsync(ct))
                .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var res in resources.Where(r => !r.IsTwinOwnResource))
            {
                if (res.Type.Equals("microsoft.app/containerapps", StringComparison.OrdinalIgnoreCase))
                {
                    var workload = await FetchContainerAppWorkloadAsync(arm, res, pinned, now, ct);
                    if (workload is not null)
                    {
                        workloads.Add(workload);
                        if (previousWorkloads.TryGetValue(workload.Id, out var prev) &&
                            !string.Equals(prev.ActiveRevisionName, workload.ActiveRevisionName, StringComparison.OrdinalIgnoreCase))
                        {
                            newEvents.Add(new ObservedEvent
                            {
                                EventId = $"rev-{workload.ResourceName}-{workload.ActiveRevisionName}",
                                IdempotencyKey = $"azure-revision:{workload.ResourceName}:{workload.ActiveRevisionName}",
                                Source = EventSourceKind.Azure,
                                SourceEventId = workload.ActiveRevisionName ?? workload.ResourceName,
                                EventType = "twin.azure.revision.changed",
                                SubjectType = "containerAppRevision",
                                SubjectId = $"{workload.ResourceName}/{workload.ActiveRevisionName}",
                                Predicate = "activated",
                                OccurredAt = workload.RevisionCreatedAt ?? now,
                                IngestedAt = now,
                                Channel = IngestionChannel.Poll,
                                CorrelationId = workload.ActiveRevisionName,
                                ChainId = workload.ActiveRevisionName,
                                PipelineId = workload.DeployableName == "ui" ? "app-ui" : "system-gitops",
                                StageName = workload.StageName,
                                DeployableName = workload.DeployableName,
                                Actor = "azure-container-apps",
                                Outcome = string.Equals(workload.HealthState, "Healthy", StringComparison.OrdinalIgnoreCase)
                                    ? EventOutcome.Success
                                    : EventOutcome.Waiting,
                                Summary = $"Container App {workload.ResourceName} activated revision {workload.ActiveRevisionName} ({workload.RunningVersion ?? "image"})",
                                PayloadJson = JsonSerializer.Serialize(workload)
                            });
                        }
                    }
                }
                else if (res.Type.Equals("microsoft.web/staticsites", StringComparison.OrdinalIgnoreCase))
                {
                    var stage = res.StageName ?? "tdd";
                    var deployable = res.DeployableName ?? "dashboard";
                    pinned.TryGetValue(stage, out var stagePins);
                    string? pinnedVer = null;
                    stagePins?.TryGetValue(deployable, out pinnedVer);

                    previousWorkloads.TryGetValue($"{stage}:{deployable}", out var prevSwa);

                    workloads.Add(new WorkloadRevisionSnapshot
                    {
                        Id = $"{stage}:{deployable}",
                        StageName = stage,
                        DeployableName = deployable,
                        HostingType = "staticwebapp",
                        HostingBoundary = "swa-centralus",
                        ResourceGroup = res.ResourceGroup,
                        ResourceName = res.Name,
                        Fqdn = res.Address?.Replace("https://", "", StringComparison.OrdinalIgnoreCase),
                        PinnedVersion = pinnedVer,
                        RunningVersion = prevSwa?.RunningVersion,
                        ProvisioningState = string.IsNullOrWhiteSpace(res.ProvisioningState) ? "Succeeded" : res.ProvisioningState,
                        RunningState = "Serving",
                        HealthState = "Healthy",
                        ObservedAt = now
                    });
                }
            }
            await state.PublishSetAsync(StateKinds.Workload, "azure", workloads, EventSourceKind.Azure, ct);

            // 5. Update EnvironmentInstances
            var existingStages = (await store.GetEnvironmentInstancesAsync(ct))
                .ToDictionary(s => s.StageName, StringComparer.OrdinalIgnoreCase);

            var stageUpdates = new List<EnvironmentInstanceSnapshot>();
            foreach (var stageDef in Cmdemo1PlatformCatalog.Stages)
            {
                existingStages.TryGetValue(stageDef.Name, out var inst);
                inst ??= new EnvironmentInstanceSnapshot
                {
                    StageName = stageDef.Name,
                    Tier = stageDef.Tier,
                    HostingBoundary = stageDef.HostingBoundary,
                    ResourceGroup = stageDef.ResourceGroup,
                    IsDeclaredOnly = stageDef.IsDeclaredOnly
                };

                var stageResources = resources.Where(r =>
                    !r.IsTwinOwnResource &&
                    string.Equals(r.StageName, stageDef.Name, StringComparison.OrdinalIgnoreCase)).ToList();

                stacksByStage.TryGetValue(stageDef.Name, out var stackInfo);

                inst.ResourceCount = stageResources.Count;
                inst.StackName = stackInfo.Name ?? (stageDef.IsDeclaredOnly ? "stack-cmdemo1-pool" : $"stack-cmdemo1-{stageDef.Name}");
                inst.StackProvisioningState = stackInfo.ProvisioningState ?? (stageDef.IsDeclaredOnly ? "NotProvisioned" : inst.StackProvisioningState);
                inst.SqlServerName = stageResources.FirstOrDefault(r => r.Type.Equals("microsoft.sql/servers", StringComparison.OrdinalIgnoreCase))?.Name;
                inst.DatabaseName = stageResources.FirstOrDefault(r => r.Type.Equals("microsoft.sql/servers/databases", StringComparison.OrdinalIgnoreCase) && !r.Name.Equals("master", StringComparison.OrdinalIgnoreCase))?.Name;
                inst.KeyVaultName = stageResources.FirstOrDefault(r => r.Type.Equals("microsoft.keyvault/vaults", StringComparison.OrdinalIgnoreCase))?.Name;
                inst.IsHealthy = !stageDef.IsDeclaredOnly;
                inst.LastEvaluatedAt = now;
                stageUpdates.Add(inst);
            }
            await state.PublishAsync(StateKinds.Environment, stageUpdates, EventSourceKind.Azure, ct);

            // 6. Resource Graph resourcechanges (last 14 days history!) -> Normalized Events
            var changes = await QueryResourceChangesAsync(arm, subId, fullBackfill ? 40 : 15, now, ct);
            newEvents.AddRange(changes);

            // 7. Check Event Grid status (system topics in both RGs)
            await RefreshEventGridStatusAsync(arm, subId, now, ct);

            // 8. Query ACR image tags (best effort)
            var acrArtifacts = await QueryAcrTagsAsync(token, now, ct);
            if (acrArtifacts.Count > 0)
            {
                await state.PublishAsync(StateKinds.Artifact, acrArtifacts, EventSourceKind.Azure, ct);
            }

            var ingested = await intake.PublishAsync(newEvents, EventSourceKind.Azure, fullBackfill ? IngestionChannel.Backfill : IngestionChannel.Poll, ct);

            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.Status = "ok";
            cursor.ItemsProcessed = resources.Count + workloads.Count + ingested;
            cursor.LastCursor = now.ToString("O", CultureInfo.InvariantCulture);
            await store.UpsertCursorAsync(cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogAzureSyncFailed(ex);
            cursor.Status = "error";
            cursor.LastError = ex.Message;
            await store.UpsertCursorAsync(cursor, ct);
            throw;
        }
    }

    public async Task<WebhookRegistrationStatus> TryEnsureEventGridSubscriptionsAsync(string twinPublicBaseUrl, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var endpointUrl = $"{twinPublicBaseUrl.TrimEnd('/')}/hooks/azure";
        if (string.IsNullOrWhiteSpace(options.Value.ClientId) ||
            string.IsNullOrWhiteSpace(options.Value.ClientSecret) ||
            string.IsNullOrWhiteSpace(options.Value.TenantId))
        {
            return new WebhookRegistrationStatus
            {
                Id = "azure:eventgrid",
                Source = "azure",
                Target = "EventGrid",
                EndpointUrl = endpointUrl,
                IsActive = false,
                Detail = "Azure credentials missing",
                CheckedAt = now
            };
        }

        try
        {
            var token = await GetArmAccessTokenAsync(ct);
            using var arm = CreateArmClient(token);
            var subId = options.Value.SubscriptionId;

            var providerState = await GetEventGridRegistrationStateAsync(arm, subId, ct);
            if (!string.Equals(providerState, "Registered", StringComparison.OrdinalIgnoreCase))
            {
                using var regResp = await arm.PostAsync(
                    $"subscriptions/{subId}/providers/Microsoft.EventGrid/register?api-version=2021-04-01",
                    new StringContent("{}", Encoding.UTF8, "application/json"),
                    ct);
                if (!regResp.IsSuccessStatusCode)
                {
                    var msg = $"Microsoft.EventGrid is '{providerState}'. RG-scoped Contributor cannot register subscription provider; Resource Graph + ARM pollers active as primary observer.";
                    var gapStatus = new WebhookRegistrationStatus
                    {
                        Id = "azure:eventgrid",
                        Source = "azure",
                        Target = "rg-cmdemo1-nonprod,rg-cmdemo1-prod",
                        EndpointUrl = endpointUrl,
                        IsActive = false,
                        Detail = msg,
                        CheckedAt = now
                    };
                    await state.PublishSetAsync(StateKinds.WebhookStatus, "azure", [gapStatus], EventSourceKind.Azure, ct);
                    return gapStatus;
                }
            }

            var webhookWithToken = string.IsNullOrWhiteSpace(options.Value.WebhookSecret)
                ? endpointUrl
                : $"{endpointUrl}?token={Uri.EscapeDataString(options.Value.WebhookSecret)}";

            var statuses = new List<WebhookRegistrationStatus>();
            foreach (var rg in ArmResourceGroups)
            {
                var topicName = $"eg-twin-{rg}";
                var topicPayload = JsonSerializer.Serialize(new
                {
                    location = "global",
                    properties = new
                    {
                        source = $"/subscriptions/{subId}/resourceGroups/{rg}",
                        topicType = "Microsoft.Resources.ResourceGroups"
                    }
                });
                using var topicResp = await arm.PutAsync(
                    $"subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.EventGrid/systemTopics/{topicName}?api-version=2022-06-15",
                    new StringContent(topicPayload, Encoding.UTF8, "application/json"),
                    ct);

                var subPayload = JsonSerializer.Serialize(new
                {
                    properties = new
                    {
                        destination = new
                        {
                            endpointType = "WebHook",
                            properties = new
                            {
                                endpointUrl = webhookWithToken
                            }
                        },
                        filter = new
                        {
                            includedEventTypes = EventGridIncludedEventTypes
                        },
                        eventDeliverySchema = "EventGridSchema"
                    }
                });
                using var subResp = await arm.PutAsync(
                    $"subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.EventGrid/systemTopics/{topicName}/eventSubscriptions/twin-observer?api-version=2022-06-15",
                    new StringContent(subPayload, Encoding.UTF8, "application/json"),
                    ct);

                var detail = subResp.IsSuccessStatusCode
                    ? "active"
                    : $"topic={(int)topicResp.StatusCode}, sub={(int)subResp.StatusCode}: {await subResp.Content.ReadAsStringAsync(ct)}";

                statuses.Add(new WebhookRegistrationStatus
                {
                    Id = $"azure:{rg}",
                    Source = "azure",
                    Target = rg,
                    EndpointUrl = endpointUrl,
                    IsActive = subResp.IsSuccessStatusCode,
                    ExternalId = topicName,
                    Detail = detail,
                    CheckedAt = now
                });
            }

            await state.PublishSetAsync(StateKinds.WebhookStatus, "azure", statuses, EventSourceKind.Azure, ct);
            return statuses.First();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogEventGridProbeFailed(ex);
            var err = new WebhookRegistrationStatus
            {
                Id = "azure:eventgrid",
                Source = "azure",
                Target = "EventGrid",
                EndpointUrl = endpointUrl,
                IsActive = false,
                Detail = ex.Message,
                CheckedAt = now
            };
            await state.PublishSetAsync(StateKinds.WebhookStatus, "azure", [err], EventSourceKind.Azure, ct);
            return err;
        }
    }

    private async Task<string> GetArmAccessTokenAsync(CancellationToken ct)
    {
        var cred = new ClientSecretCredential(
            options.Value.TenantId,
            options.Value.ClientId,
            options.Value.ClientSecret);
        var tokenResult = await cred.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]),
            ct);
        return tokenResult.Token;
    }

    private HttpClient CreateArmClient(string bearerToken)
    {
        var client = httpClientFactory.CreateClient("azure-arm");
        client.BaseAddress = new Uri("https://management.azure.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    private static async Task FetchResourceHealthAsync(
        HttpClient arm,
        string subId,
        string rg,
        Dictionary<string, (string Status, string? Summary)> healthById,
        CancellationToken ct)
    {
        using var resp = await arm.GetAsync(
            $"subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.ResourceHealth/availabilityStatuses?api-version=2022-10-01",
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            return;
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var arr))
        {
            return;
        }
        foreach (var item in arr.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            const string suffix = "/providers/Microsoft.ResourceHealth/availabilityStatuses/current";
            var idx = id.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            var targetId = idx > 0 ? id[..idx] : id;

            if (item.TryGetProperty("properties", out var props))
            {
                var state = props.TryGetProperty("availabilityState", out var av) ? av.GetString() ?? "Unknown" : "Unknown";
                var summary = props.TryGetProperty("summary", out var sm) ? sm.GetString() : null;
                healthById[targetId] = (state.ToLowerInvariant(), summary);
            }
        }
    }

    private static async Task<List<AzureResourceSnapshot>> QueryResourcesAsync(
        HttpClient arm,
        string subId,
        Dictionary<string, (string Status, string? Summary)> healthById,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var kql = """
            resources
            | where resourceGroup in~ ("rg-cmdemo1-nonprod", "rg-cmdemo1-prod")
            | project id, name, type, resourceGroup, location, tags,
                      provisioningState = tostring(properties.provisioningState),
                      fqdn = tostring(properties.configuration.ingress.fqdn),
                      defaultHostname = tostring(properties.defaultHostname),
                      managedEnvironmentId = tostring(properties.managedEnvironmentId)
            | order by resourceGroup asc, type asc, name asc
            """;

        var body = JsonSerializer.Serialize(new
        {
            subscriptions = new[] { subId },
            query = kql
        });

        using var resp = await arm.PostAsync(
            "providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var list = new List<AzureResourceSnapshot>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var row in data.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString() ?? "";
            var name = row.GetProperty("name").GetString() ?? "";
            var type = row.GetProperty("type").GetString() ?? "";
            var rg = row.GetProperty("resourceGroup").GetString() ?? "";
            var location = row.GetProperty("location").GetString() ?? "";
            var provState = row.TryGetProperty("provisioningState", out var ps) ? ps.GetString() : null;
            var fqdn = row.TryGetProperty("fqdn", out var fq) ? fq.GetString() : null;
            var defaultHost = row.TryGetProperty("defaultHostname", out var dh) ? dh.GetString() : null;
            var managedEnv = row.TryGetProperty("managedEnvironmentId", out var me) ? me.GetString() : null;
            var tagsEl = row.TryGetProperty("tags", out var tg) ? tg : default;

            string? tagEnv = null;
            string? tagDeployable = null;
            string? tagPurpose = null;
            if (tagsEl.ValueKind == JsonValueKind.Object)
            {
                tagEnv = tagsEl.TryGetProperty("environment", out var te) ? te.GetString() : null;
                tagDeployable = tagsEl.TryGetProperty("deployable", out var td) ? td.GetString() : null;
                tagPurpose = tagsEl.TryGetProperty("purpose", out var tp) ? tp.GetString() : null;
            }

            var isTwin = name.Contains("twin", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(tagPurpose, "digital-twin", StringComparison.OrdinalIgnoreCase);

            var stage = !string.IsNullOrWhiteSpace(tagEnv)
                ? tagEnv
                : Cmdemo1PlatformCatalog.InferStageFromAzureResourceName(name, rg);

            var address = !string.IsNullOrWhiteSpace(fqdn) ? $"https://{fqdn}"
                : !string.IsNullOrWhiteSpace(defaultHost) ? $"https://{defaultHost}"
                : null;

            healthById.TryGetValue(id, out var rh);

            list.Add(new AzureResourceSnapshot
            {
                ResourceId = id,
                Name = name,
                Type = type,
                ResourceGroup = rg,
                Location = location,
                StageName = stage,
                DeployableName = tagDeployable ?? (name.Contains("-ui", StringComparison.OrdinalIgnoreCase) ? "ui" : name.Contains("-dashboard", StringComparison.OrdinalIgnoreCase) ? "dashboard" : null),
                ProvisioningState = provState,
                ResourceHealthStatus = string.IsNullOrWhiteSpace(rh.Status) ? (string.IsNullOrWhiteSpace(provState) ? "unknown" : provState.ToLowerInvariant()) : rh.Status,
                ResourceHealthSummary = rh.Summary,
                Address = address,
                ManagedEnvironmentId = managedEnv,
                IsTwinOwnResource = isTwin,
                TagsJson = StableJson.SortedObject(tagsEl),
                ObservedAt = now
            });
        }

        return list;
    }

    private static async Task<Dictionary<string, (string Name, string ProvisioningState)>> FetchStacksAsync(
        HttpClient arm,
        string subId,
        CancellationToken ct)
    {
        var map = new Dictionary<string, (string Name, string ProvisioningState)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rg in ArmResourceGroups)
        {
            using var resp = await arm.GetAsync(
                $"subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Resources/deploymentStacks?api-version=2024-03-01",
                ct);
            if (!resp.IsSuccessStatusCode) continue;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out var arr)) continue;
            foreach (var st in arr.EnumerateArray())
            {
                var name = st.GetProperty("name").GetString() ?? "";
                var prov = st.TryGetProperty("properties", out var p) && p.TryGetProperty("provisioningState", out var ps)
                    ? ps.GetString() ?? "succeeded"
                    : "succeeded";
                if (name.StartsWith("stack-cmdemo1-", StringComparison.OrdinalIgnoreCase))
                {
                    var stage = name["stack-cmdemo1-".Length..];
                    map[stage] = (name, prov);
                }
            }
        }
        return map;
    }

    private static async Task<WorkloadRevisionSnapshot?> FetchContainerAppWorkloadAsync(
        HttpClient arm,
        AzureResourceSnapshot res,
        IReadOnlyDictionary<string, Dictionary<string, string>> pinned,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var path = res.ResourceId.TrimStart('/');
        using var appResp = await arm.GetAsync($"{path}?api-version=2025-01-01", ct);
        if (!appResp.IsSuccessStatusCode)
        {
            return null;
        }

        var appJson = await appResp.Content.ReadAsStringAsync(ct);
        using var appDoc = JsonDocument.Parse(appJson);
        var props = appDoc.RootElement.GetProperty("properties");

        var envId = props.TryGetProperty("environmentId", out var eid) ? eid.GetString() ?? "" : "";
        var hostingBoundary = AzureEventNormalizer.ExtractResourceName(envId);
        var fqdn = props.TryGetProperty("configuration", out var cfg) && cfg.TryGetProperty("ingress", out var ing) && ing.TryGetProperty("fqdn", out var fq)
            ? fq.GetString()
            : null;
        var latestRev = props.TryGetProperty("latestRevisionName", out var lr) ? lr.GetString() : null;
        var readyRev = props.TryGetProperty("latestReadyRevisionName", out var lrr) ? lrr.GetString() : null;
        var appProv = props.TryGetProperty("provisioningState", out var ps) ? ps.GetString() : null;
        var appRun = props.TryGetProperty("runningStatus", out var rs) ? rs.GetString() : null;
        var image = props.TryGetProperty("template", out var tpl) &&
                    tpl.TryGetProperty("containers", out var ctrs) &&
                    ctrs.GetArrayLength() > 0 &&
                    ctrs[0].TryGetProperty("image", out var im)
            ? im.GetString()
            : null;
        int? minRep = tpl.ValueKind == JsonValueKind.Object && tpl.TryGetProperty("scale", out var sc) && sc.TryGetProperty("minReplicas", out var mr) && mr.TryGetInt32(out var mrv) ? mrv : null;
        int? maxRep = tpl.ValueKind == JsonValueKind.Object && tpl.TryGetProperty("scale", out var sc2) && sc2.TryGetProperty("maxReplicas", out var mxr) && mxr.TryGetInt32(out var mxrv) ? mxrv : null;

        string? revProv = appProv;
        string? revRun = appRun;
        string? revHealth = "Healthy";
        int? replicas = null;
        int? trafficWeight = 100;
        DateTimeOffset? revCreatedAt = null;

        if (!string.IsNullOrWhiteSpace(latestRev))
        {
            using var revResp = await arm.GetAsync($"{path}/revisions/{latestRev}?api-version=2025-01-01", ct);
            if (revResp.IsSuccessStatusCode)
            {
                var revJson = await revResp.Content.ReadAsStringAsync(ct);
                using var revDoc = JsonDocument.Parse(revJson);
                if (revDoc.RootElement.TryGetProperty("properties", out var rProps))
                {
                    revProv = rProps.TryGetProperty("provisioningState", out var rps) ? rps.GetString() : revProv;
                    revRun = rProps.TryGetProperty("runningState", out var rrs) ? rrs.GetString() : revRun;
                    revHealth = rProps.TryGetProperty("healthState", out var rhs) ? rhs.GetString() : revHealth;
                    replicas = rProps.TryGetProperty("replicas", out var rrep) && rrep.TryGetInt32(out var repv) ? repv : null;
                    trafficWeight = rProps.TryGetProperty("trafficWeight", out var rtw) && rtw.TryGetInt32(out var twv) ? twv : null;
                    revCreatedAt = rProps.TryGetProperty("createdTime", out var rct) && DateTimeOffset.TryParse(rct.GetString(), out var dt) ? dt.ToUniversalTime() : null;
                }
            }
        }

        var stage = res.StageName ?? "tdd";
        var deployable = res.DeployableName ?? "ui";
        string? pinnedVer = null;
        if (pinned.TryGetValue(stage, out var stagePins))
        {
            stagePins.TryGetValue(deployable, out pinnedVer);
        }

        var imageTag = image is not null && image.Contains(':', StringComparison.Ordinal)
            ? image[(image.LastIndexOf(':') + 1)..]
            : null;

        return new WorkloadRevisionSnapshot
        {
            Id = $"{stage}:{deployable}",
            StageName = stage,
            DeployableName = deployable,
            HostingType = "containerapp",
            HostingBoundary = hostingBoundary,
            ResourceGroup = res.ResourceGroup,
            ResourceName = res.Name,
            Fqdn = fqdn,
            PinnedVersion = pinnedVer,
            RunningVersion = imageTag,
            ActiveRevisionName = latestRev,
            LatestReadyRevisionName = readyRev,
            ProvisioningState = revProv,
            RunningState = revRun,
            HealthState = revHealth,
            Replicas = replicas,
            MinReplicas = minRep,
            MaxReplicas = maxRep,
            TrafficWeight = trafficWeight,
            RunningImage = image,
            RevisionCreatedAt = revCreatedAt,
            ObservedAt = now
        };
    }

    private static async Task<List<ObservedEvent>> QueryResourceChangesAsync(
        HttpClient arm,
        string subId,
        int take,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var kql = $"""
            resourcechanges
            | where resourceGroup in~ ("rg-cmdemo1-nonprod", "rg-cmdemo1-prod")
            | project id,
                      targetResourceId = tostring(properties.targetResourceId),
                      targetResourceType = tostring(properties.targetResourceType),
                      changeType = tostring(properties.changeType),
                      timestamp = tostring(properties.changeAttributes.timestamp),
                      changedBy = tostring(properties.changeAttributes.changedBy),
                      operation = tostring(properties.changeAttributes.operation)
            | order by timestamp desc
            | take {take}
            """;

        var body = JsonSerializer.Serialize(new
        {
            subscriptions = new[] { subId },
            query = kql
        });

        var list = new List<ObservedEvent>();
        using var resp = await arm.PostAsync(
            "providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            return list;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var row in data.EnumerateArray())
        {
            var id = row.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            var targetId = row.TryGetProperty("targetResourceId", out var tr) ? tr.GetString() ?? "" : "";
            var targetType = row.TryGetProperty("targetResourceType", out var tt) ? tt.GetString() ?? "" : "";
            var changeType = row.TryGetProperty("changeType", out var ch) ? ch.GetString() ?? "Update" : "Update";
            var operation = row.TryGetProperty("operation", out var op) ? op.GetString() ?? changeType : changeType;
            var changedBy = row.TryGetProperty("changedBy", out var cb) ? cb.GetString() : null;
            var ts = row.TryGetProperty("timestamp", out var tsEl) && DateTimeOffset.TryParse(tsEl.GetString(), out var dt) ? dt.ToUniversalTime() : now;

            list.Add(AzureEventNormalizer.NormalizeResourceChange(
                id,
                targetId,
                targetType,
                changeType,
                operation,
                changedBy,
                ts,
                now,
                row.GetRawText()));
        }

        return list;
    }

    private async Task RefreshEventGridStatusAsync(HttpClient arm, string subId, DateTimeOffset now, CancellationToken ct)
    {
        var providerState = await GetEventGridRegistrationStateAsync(arm, subId, ct);
        if (!string.Equals(providerState, "Registered", StringComparison.OrdinalIgnoreCase))
        {
            await state.PublishSetAsync(StateKinds.WebhookStatus, "azure", [
                new WebhookRegistrationStatus
                {
                    Id = "azure:eventgrid",
                    Source = "azure",
                    Target = "rg-cmdemo1-nonprod,rg-cmdemo1-prod",
                    EndpointUrl = "/hooks/azure",
                    IsActive = false,
                    Detail = $"Microsoft.EventGrid provider is {providerState}; Resource Graph & ARM reconciliation pollers active",
                    CheckedAt = now
                }
            ], EventSourceKind.Azure, ct);
            return;
        }

        var list = new List<WebhookRegistrationStatus>();
        foreach (var rg in ArmResourceGroups)
        {
            using var resp = await arm.GetAsync(
                $"subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.EventGrid/systemTopics?api-version=2022-06-15",
                ct);
            var active = false;
            var detail = "no system topic";
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.GetArrayLength() > 0)
                {
                    active = true;
                    detail = $"{arr.GetArrayLength()} system topic(s) present";
                }
            }
            list.Add(new WebhookRegistrationStatus
            {
                Id = $"azure:{rg}",
                Source = "azure",
                Target = rg,
                EndpointUrl = "/hooks/azure",
                IsActive = active,
                Detail = detail,
                CheckedAt = now
            });
        }
        await state.PublishSetAsync(StateKinds.WebhookStatus, "azure", list, EventSourceKind.Azure, ct);
    }

    private static async Task<string> GetEventGridRegistrationStateAsync(HttpClient arm, string subId, CancellationToken ct)
    {
        using var resp = await arm.GetAsync($"subscriptions/{subId}/providers/Microsoft.EventGrid?api-version=2021-04-01", ct);
        if (!resp.IsSuccessStatusCode) return "Unknown";
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("registrationState", out var st) ? st.GetString() ?? "Unknown" : "Unknown";
    }

    private async Task<List<ArtifactPackageSnapshot>> QueryAcrTagsAsync(string armToken, DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<ArtifactPackageSnapshot>();
        try
        {
            using var acr = httpClientFactory.CreateClient("acr");
            acr.BaseAddress = new Uri($"https://{Cmdemo1PlatformCatalog.AcrLoginServer}");

            var exchangeContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "access_token",
                ["service"] = Cmdemo1PlatformCatalog.AcrLoginServer,
                ["tenant"] = options.Value.TenantId,
                ["access_token"] = armToken
            });
            using var exResp = await acr.PostAsync("/oauth2/exchange", exchangeContent, ct);
            if (!exResp.IsSuccessStatusCode) return list;
            using var exDoc = JsonDocument.Parse(await exResp.Content.ReadAsStringAsync(ct));
            var refreshToken = exDoc.RootElement.GetProperty("refresh_token").GetString();
            if (string.IsNullOrWhiteSpace(refreshToken)) return list;

            var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["service"] = Cmdemo1PlatformCatalog.AcrLoginServer,
                ["scope"] = "repository:cmdemo1/ui:metadata_read",
                ["refresh_token"] = refreshToken
            });
            using var tkResp = await acr.PostAsync("/oauth2/token", tokenContent, ct);
            if (!tkResp.IsSuccessStatusCode) return list;
            using var tkDoc = JsonDocument.Parse(await tkResp.Content.ReadAsStringAsync(ct));
            var accessToken = tkDoc.RootElement.GetProperty("access_token").GetString();

            acr.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var tagsResp = await acr.GetAsync("/acr/v1/cmdemo1%2Fui/_tags?n=15&orderby=timedesc", ct);
            if (!tagsResp.IsSuccessStatusCode) return list;
            using var tagsDoc = JsonDocument.Parse(await tagsResp.Content.ReadAsStringAsync(ct));
            if (tagsDoc.RootElement.TryGetProperty("tags", out var tagsArr))
            {
                foreach (var t in tagsArr.EnumerateArray())
                {
                    var tag = t.GetProperty("name").GetString() ?? "";
                    var created = t.TryGetProperty("createdTime", out var ctEl) && DateTimeOffset.TryParse(ctEl.GetString(), out var dt) ? dt.ToUniversalTime() : now;
                    list.Add(new ArtifactPackageSnapshot
                    {
                        Id = $"ContainerImage:cmdemo1/ui:{tag}",
                        Kind = "ContainerImage",
                        Name = "cmdemo1/ui",
                        Version = tag,
                        RegistryOrFeed = Cmdemo1PlatformCatalog.AcrLoginServer,
                        PipelineId = "app-ui",
                        PublishedAt = created
                    });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogAcrSkipped(ex);
        }
        return list;
    }
}
