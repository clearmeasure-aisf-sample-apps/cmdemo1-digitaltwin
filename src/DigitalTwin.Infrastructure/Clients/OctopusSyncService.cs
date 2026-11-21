using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalTwin.Application.Abstractions;
using DigitalTwin.Application.Services;
using DigitalTwin.Domain.BuildAndArtifact;
using DigitalTwin.Domain.Hierarchy;
using DigitalTwin.Domain.Observation;
using DigitalTwin.Domain.ReleaseAndDelivery;
using DigitalTwin.Domain.Normalization;
using DigitalTwin.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Infrastructure.Clients;

public sealed partial class OctopusSyncService(
    IHttpClientFactory httpClientFactory,
    ITwinStore store,
    EventIngestionService ingestion,
    IOptions<TwinOptions> options,
    TimeProvider timeProvider,
    ILogger<OctopusSyncService> logger) : IOctopusSyncService
{
    private const string TwinSubscriptionName = "cmdemo1-digitaltwin";

    [GeneratedRegex(@"\b(?<sha>[0-9a-f]{40})\b", RegexOptions.Compiled)]
    private static partial Regex CommitShaRegex();

    [GeneratedRegex(@"\bcommitted (?<time>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))", RegexOptions.Compiled)]
    private static partial Regex CommittedTimeRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Octopus sync failed")]
    private partial void LogOctopusSyncFailed(Exception ex);

    public async Task SyncAsync(bool fullBackfill, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var cursor = new IngestionCursor
        {
            SourceKey = "octopus",
            LastAttemptAt = now,
            Status = "running"
        };

        try
        {
            if (string.IsNullOrWhiteSpace(options.Value.OctopusApiKey))
            {
                cursor.Status = "skipped";
                cursor.LastError = "OCTOPUS_API_KEY not configured";
                await store.UpsertCursorAsync(cursor, ct);
                return;
            }

            using var client = CreateClient();
            var space = Cmdemo1PlatformCatalog.OctopusSpaceId;
            var baseUri = options.Value.OctopusUrl.TrimEnd('/');

            var projectsJson = await GetRequiredAsync(client, $"/api/{space}/projects?take=100", ct);
            var envsJson = await GetRequiredAsync(client, $"/api/{space}/environments?take=100", ct);
            var channelsJson = await GetRequiredAsync(client, $"/api/{space}/channels?take=100", ct);
            var releasesJson = await GetRequiredAsync(client, $"/api/{space}/releases?take={(fullBackfill ? 100 : 50)}", ct);
            var deploymentsJson = await GetRequiredAsync(client, $"/api/{space}/deployments?take={(fullBackfill ? 100 : 60)}", ct);
            var deployTasksJson = await GetRequiredAsync(client, $"/api/{space}/tasks?name=Deploy&take=100", ct);
            var runbookTasksJson = await GetOptionalAsync(client, $"/api/{space}/tasks?name=RunbookRun&take=60", ct);
            var interruptionsJson = await GetRequiredAsync(client, $"/api/{space}/interruptions?take=50", ct);
            var eventsJson = await GetRequiredAsync(client, $"/api/{space}/events?take={(fullBackfill ? 100 : 50)}", ct);
            var runbooksJson = await GetOptionalAsync(client, $"/api/{space}/runbooks?take=50", ct);
            var runbookRunsJson = await GetOptionalAsync(client, $"/api/{space}/runbookRuns?take=40", ct);
            var freezesJson = await GetOptionalAsync(client, "/api/deploymentfreezes?skip=0&take=50", ct);
            var subsJson = await GetOptionalAsync(client, $"/api/{space}/subscriptions?take=50", ct);
            var packagesJson = await GetOptionalAsync(client, $"/api/{space}/packages?take=30", ct);

            var projectById = new Dictionary<string, (string Name, string Slug)>(StringComparer.OrdinalIgnoreCase);
            using (var pDoc = JsonDocument.Parse(projectsJson))
            {
                foreach (var p in pDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    projectById[p.GetProperty("Id").GetString()!] = (p.GetProperty("Name").GetString()!, p.GetProperty("Slug").GetString()!);
                }
            }

            var envById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var eDoc = JsonDocument.Parse(envsJson))
            {
                foreach (var e in eDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    envById[e.GetProperty("Id").GetString()!] = e.GetProperty("Name").GetString()!;
                }
            }

            var channelById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var cDoc = JsonDocument.Parse(channelsJson))
            {
                foreach (var c in cDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    channelById[c.GetProperty("Id").GetString()!] = c.GetProperty("Name").GetString()!;
                }
            }

            // Releases
            var releaseMap = new Dictionary<string, ReleaseSnapshot>(StringComparer.OrdinalIgnoreCase);
            using (var rDoc = JsonDocument.Parse(releasesJson))
            {
                foreach (var r in rDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    var rid = r.GetProperty("Id").GetString()!;
                    var pid = r.GetProperty("ProjectId").GetString()!;
                    var cid = r.TryGetProperty("ChannelId", out var ch) ? ch.GetString() : null;
                    var version = r.GetProperty("Version").GetString() ?? "";
                    var notes = r.TryGetProperty("ReleaseNotes", out var rn) ? rn.GetString() : null;
                    var assembled = r.TryGetProperty("Assembled", out var asmb) && DateTimeOffset.TryParse(asmb.GetString(), out var adt) ? adt.ToUniversalTime() : now;
                    var projSlug = projectById.TryGetValue(pid, out var pInfo) ? pInfo.Slug : pid;
                    var sha = notes is not null ? CommitShaRegex().Match(notes).Groups["sha"].Value : null;
                    if (string.IsNullOrWhiteSpace(sha)) sha = null;
                    DateTimeOffset? committedAt = null;
                    if (notes is not null)
                    {
                        var m = CommittedTimeRegex().Match(notes);
                        if (m.Success && DateTimeOffset.TryParse(m.Groups["time"].Value, out var cdt))
                        {
                            committedAt = cdt.ToUniversalTime();
                        }
                    }

                    releaseMap[rid] = new ReleaseSnapshot
                    {
                        ReleaseId = rid,
                        ProjectSlug = projSlug,
                        DeployableName = Cmdemo1PlatformCatalog.ResolveDeployableFromOctopusProject(projSlug),
                        PipelineId = Cmdemo1PlatformCatalog.ResolvePipelineIdFromOctopusProject(projSlug),
                        ChannelName = cid is not null && channelById.TryGetValue(cid, out var cn) ? cn : "Default",
                        Version = version,
                        GitCommitSha = sha,
                        CommittedAt = committedAt,
                        CreatedAt = assembled,
                        ReleaseNotes = notes,
                        ReleaseUrl = $"{baseUri}/app#/{space}/projects/{projSlug}/deployments/releases/{version}"
                    };
                }
            }
            await store.UpsertReleasesAsync(releaseMap.Values.ToList(), ct);

            // Tasks (Deploy + RunbookRun) + Interruptions
            var taskById = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            using (var tasksDoc = JsonDocument.Parse(deployTasksJson))
            {
                foreach (var t in tasksDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    taskById[t.GetProperty("Id").GetString()!] = t.Clone();
                }
            }
            if (!string.IsNullOrWhiteSpace(runbookTasksJson))
            {
                using var rbTasksDoc = JsonDocument.Parse(runbookTasksJson);
                foreach (var t in rbTasksDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    taskById[t.GetProperty("Id").GetString()!] = t.Clone();
                }
            }

            var interruptionByTask = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);
            using (var iDoc = JsonDocument.Parse(interruptionsJson))
            {
                foreach (var intr in iDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    if (intr.TryGetProperty("RelatedDocumentIds", out var rels))
                    {
                        foreach (var rel in rels.EnumerateArray())
                        {
                            var s = rel.GetString();
                            if (s is not null && s.StartsWith("ServerTasks-", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!interruptionByTask.TryGetValue(s, out var list))
                                {
                                    list = [];
                                    interruptionByTask[s] = list;
                                }
                                list.Add(intr.Clone());
                            }
                        }
                    }
                }
            }

            // Deployments
            var deliverySnapshots = new List<DeliveryExecutionSnapshot>();
            using (var dDoc = JsonDocument.Parse(deploymentsJson))
            {
                foreach (var d in dDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    var depId = d.GetProperty("Id").GetString()!;
                    var taskId = d.GetProperty("TaskId").GetString()!;
                    var projectId = d.GetProperty("ProjectId").GetString()!;
                    var envId = d.GetProperty("EnvironmentId").GetString()!;
                    var releaseId = d.GetProperty("ReleaseId").GetString()!;
                    var channelId = d.TryGetProperty("ChannelId", out var ch) ? ch.GetString() : null;
                    var deployedBy = d.TryGetProperty("DeployedBy", out var db) ? db.GetString() : null;
                    var createdAt = d.TryGetProperty("Created", out var cr) && DateTimeOffset.TryParse(cr.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;

                    var projSlug = projectById.TryGetValue(projectId, out var pInfo) ? pInfo.Slug : projectId;
                    var stageName = envById.TryGetValue(envId, out var en) ? en : envId;
                    releaseMap.TryGetValue(releaseId, out var rel);

                    taskById.TryGetValue(taskId, out var taskEl);
                    var taskState = taskEl.ValueKind == JsonValueKind.Object && taskEl.TryGetProperty("State", out var ts) ? ts.GetString() ?? "Queued" : "Success";
                    var hasPending = taskEl.ValueKind == JsonValueKind.Object && taskEl.TryGetProperty("HasPendingInterruptions", out var hp) && hp.ValueKind == JsonValueKind.True;
                    DateTimeOffset? startedAt = taskEl.ValueKind == JsonValueKind.Object && taskEl.TryGetProperty("StartTime", out var st) && st.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(st.GetString(), out var sdt) ? sdt.ToUniversalTime() : null;
                    DateTimeOffset? finishedAt = taskEl.ValueKind == JsonValueKind.Object && taskEl.TryGetProperty("CompletedTime", out var ft) && ft.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ft.GetString(), out var fdt) ? fdt.ToUniversalTime() : null;

                    interruptionByTask.TryGetValue(taskId, out var intrs);
                    var pendingHold = intrs?.FirstOrDefault(x => x.TryGetProperty("IsPending", out var ip) && ip.ValueKind == JsonValueKind.True);
                    var hasGuided = intrs?.Any(x => string.Equals(x.TryGetProperty("Type", out var tp) ? tp.GetString() : "", "GuidedFailure", StringComparison.OrdinalIgnoreCase)) == true;
                    var answeredSignOff = intrs?.FirstOrDefault(x =>
                        (!x.TryGetProperty("IsPending", out var ip) || ip.ValueKind == JsonValueKind.False) &&
                        string.Equals(x.TryGetProperty("Title", out var tt) ? tt.GetString() : "", "Sign-off", StringComparison.OrdinalIgnoreCase));

                    string? signOffReason = null;
                    if (answeredSignOff is { ValueKind: JsonValueKind.Object } so &&
                        so.TryGetProperty("Form", out var fm) &&
                        fm.TryGetProperty("Values", out var vals) &&
                        vals.TryGetProperty("Notes", out var notesEl))
                    {
                        signOffReason = notesEl.GetString();
                    }

                    var normalizedState = (taskState, pendingHold is { ValueKind: JsonValueKind.Object }) switch
                    {
                        ("Queued", true) => "waiting",
                        ("Executing", true) => "waiting",
                        ("Queued", false) => "queued",
                        ("Success", _) => "succeeded",
                        ("Failed", _) or ("TimedOut", _) => "failed",
                        ("Canceled", _) => "canceled",
                        _ => "executing"
                    };

                    double? leadHours = null;
                    if (finishedAt is not null && rel?.CommittedAt is not null && finishedAt.Value >= rel.CommittedAt.Value)
                    {
                        leadHours = Math.Round((finishedAt.Value - rel.CommittedAt.Value).TotalHours, 2);
                    }

                    deliverySnapshots.Add(new DeliveryExecutionSnapshot
                    {
                        DeploymentId = depId,
                        TaskId = taskId,
                        ProjectSlug = projSlug,
                        DeployableName = Cmdemo1PlatformCatalog.ResolveDeployableFromOctopusProject(projSlug),
                        PipelineId = Cmdemo1PlatformCatalog.ResolvePipelineIdFromOctopusProject(projSlug),
                        StageName = stageName,
                        ReleaseVersion = rel?.Version ?? releaseId,
                        ChannelName = channelId is not null && channelById.TryGetValue(channelId, out var cn) ? cn : rel?.ChannelName ?? "Default",
                        State = normalizedState,
                        QueuedAt = createdAt,
                        StartedAt = startedAt,
                        FinishedAt = finishedAt,
                        StartedBy = SanitizeUser(deployedBy),
                        SignedOffBy = signOffReason is not null ? SanitizeUser(deployedBy) : null,
                        SignOffReason = signOffReason,
                        IsPersonSignOff = signOffReason is not null && !string.Equals(deployedBy, "cmdemo1-github", StringComparison.OrdinalIgnoreCase),
                        HasPendingInterruption = hasPending,
                        HasGuidedFailure = hasGuided,
                        WaitsForKind = pendingHold is { ValueKind: JsonValueKind.Object } ph ? ph.GetProperty("Type").GetString() : null,
                        WaitsForTitle = pendingHold is { ValueKind: JsonValueKind.Object } ph2 ? ph2.GetProperty("Title").GetString() : null,
                        GitCommitSha = rel?.GitCommitSha,
                        CommittedAt = rel?.CommittedAt,
                        LeadTimeHours = leadHours,
                        TaskUrl = $"{baseUri}/app#/{space}/tasks/{taskId}"
                    });
                }
            }
            await store.UpsertDeliveriesAsync(deliverySnapshots, ct);

            // Runbooks
            if (!string.IsNullOrWhiteSpace(runbooksJson) && !string.IsNullOrWhiteSpace(runbookRunsJson))
            {
                var rbNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var rbDoc = JsonDocument.Parse(runbooksJson))
                {
                    foreach (var rb in rbDoc.RootElement.GetProperty("Items").EnumerateArray())
                    {
                        rbNames[rb.GetProperty("Id").GetString()!] = rb.GetProperty("Name").GetString()!;
                    }
                }

                var runbookSnapshots = new List<RunbookExecutionSnapshot>();
                using var rrDoc = JsonDocument.Parse(runbookRunsJson);
                foreach (var rr in rrDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    var taskId = rr.GetProperty("TaskId").GetString()!;
                    var rbId = rr.GetProperty("RunbookId").GetString()!;
                    var pid = rr.GetProperty("ProjectId").GetString()!;
                    var eid = rr.GetProperty("EnvironmentId").GetString()!;
                    var createdAt = rr.TryGetProperty("Created", out var cr) && DateTimeOffset.TryParse(cr.GetString(), out var cdt) ? cdt.ToUniversalTime() : now;
                    taskById.TryGetValue(taskId, out var taskEl);
                    var state = taskEl.ValueKind == JsonValueKind.Object && taskEl.TryGetProperty("State", out var st) ? st.GetString() ?? "Success" : "Success";
                    DateTimeOffset? finishedAt = taskEl.ValueKind == JsonValueKind.Object && taskEl.TryGetProperty("CompletedTime", out var ft) && ft.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ft.GetString(), out var fdt) ? fdt.ToUniversalTime() : null;

                    runbookSnapshots.Add(new RunbookExecutionSnapshot
                    {
                        TaskId = taskId,
                        RunbookName = rbNames.TryGetValue(rbId, out var rn) ? rn : rbId,
                        ProjectSlug = projectById.TryGetValue(pid, out var pInfo) ? pInfo.Slug : pid,
                        StageName = envById.TryGetValue(eid, out var en) ? en : eid,
                        State = state.ToLowerInvariant(),
                        CreatedAt = createdAt,
                        FinishedAt = finishedAt,
                        Summary = rr.TryGetProperty("Name", out var nm) ? nm.GetString() : null,
                        TaskUrl = $"{baseUri}/app#/{space}/tasks/{taskId}"
                    });
                }
                await store.UpsertRunbooksAsync(runbookSnapshots, ct);
            }

            // Packages
            if (!string.IsNullOrWhiteSpace(packagesJson))
            {
                var pkgs = new List<ArtifactPackageSnapshot>();
                using var pkgDoc = JsonDocument.Parse(packagesJson);
                foreach (var pkg in pkgDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    var pkgId = pkg.GetProperty("PackageId").GetString() ?? "";
                    var ver = pkg.GetProperty("Version").GetString() ?? "";
                    var pub = pkg.TryGetProperty("Published", out var pb) && DateTimeOffset.TryParse(pb.GetString(), out var pdt) ? pdt.ToUniversalTime() : now;
                    pkgs.Add(new ArtifactPackageSnapshot
                    {
                        Id = $"OctopusPackage:{pkgId}:{ver}",
                        Kind = "OctopusPackage",
                        Name = pkgId,
                        Version = ver,
                        RegistryOrFeed = "octopus-builtin-feed",
                        PipelineId = Cmdemo1PlatformCatalog.ResolvePipelineIdFromOctopusProject(pkgId),
                        PublishedAt = pub
                    });
                }
                await store.UpsertArtifactsAsync(pkgs, ct);
            }

            // Freezes
            if (!string.IsNullOrWhiteSpace(freezesJson))
            {
                var freezes = new List<DeploymentFreezeSnapshot>();
                using var fDoc = JsonDocument.Parse(freezesJson);
                if (fDoc.RootElement.TryGetProperty("DeploymentFreezes", out var dfArr))
                {
                    foreach (var f in dfArr.EnumerateArray())
                    {
                        var scopeProjects = new List<string>();
                        if (f.TryGetProperty("ProjectEnvironmentScope", out var pes) && pes.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in pes.EnumerateObject())
                            {
                                if (projectById.TryGetValue(prop.Name, out var pInfo))
                                {
                                    scopeProjects.Add(pInfo.Slug);
                                }
                            }
                        }
                        if (scopeProjects.Count == 0)
                        {
                            continue; // Instance freeze for another space
                        }

                        var start = f.TryGetProperty("Start", out var st) && DateTimeOffset.TryParse(st.GetString(), out var sdt) ? sdt.ToUniversalTime() : now;
                        var end = f.TryGetProperty("End", out var en) && DateTimeOffset.TryParse(en.GetString(), out var edt) ? edt.ToUniversalTime() : now;
                        freezes.Add(new DeploymentFreezeSnapshot
                        {
                            FreezeId = f.GetProperty("Id").GetString()!,
                            Name = f.GetProperty("Name").GetString() ?? "freeze",
                            StartsAt = start,
                            EndsAt = end,
                            IsActive = start <= now && end >= now,
                            ProjectsJson = JsonSerializer.Serialize(scopeProjects),
                            UpdatedAt = now
                        });
                    }
                }
                await store.ReplaceFreezesAsync(freezes, ct);
            }

            // Subscription status
            if (!string.IsNullOrWhiteSpace(subsJson))
            {
                var subStatuses = new List<WebhookRegistrationStatus>();
                using var sDoc = JsonDocument.Parse(subsJson);
                foreach (var sub in sDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    var name = sub.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.Equals(TwinSubscriptionName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var disabled = sub.TryGetProperty("IsDisabled", out var dis) && dis.ValueKind == JsonValueKind.True;
                    var hookUri = sub.TryGetProperty("EventNotificationSubscription", out var ens) && ens.TryGetProperty("WebhookURI", out var wu)
                        ? wu.GetString() ?? ""
                        : "";
                    var lastProc = ens.ValueKind == JsonValueKind.Object && ens.TryGetProperty("WebhookLastProcessed", out var lp) && DateTimeOffset.TryParse(lp.GetString(), out var lpdt)
                        ? lpdt.ToUniversalTime()
                        : (DateTimeOffset?)null;
                    subStatuses.Add(new WebhookRegistrationStatus
                    {
                        Id = $"octopus:{sub.GetProperty("Id").GetString()}",
                        Source = "octopus",
                        Target = Cmdemo1PlatformCatalog.OctopusSpaceId,
                        EndpointUrl = hookUri,
                        IsActive = !disabled,
                        ExternalId = sub.GetProperty("Id").GetString(),
                        LastDeliveryAt = lastProc,
                        Detail = disabled ? "disabled" : "active",
                        CheckedAt = now
                    });
                }
                await store.ReplaceWebhookStatusesAsync("octopus", subStatuses, ct);
            }

            // Audit events -> Normalized Domain Events
            var projNameMap = projectById.ToDictionary(kv => kv.Key, kv => kv.Value.Slug, StringComparer.OrdinalIgnoreCase);
            var normalizedEvents = new List<ObservedEvent>();
            using (var evDoc = JsonDocument.Parse(eventsJson))
            {
                foreach (var ev in evDoc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    var cat = ev.TryGetProperty("Category", out var cEl) ? cEl.GetString() : null;
                    var msg = ev.TryGetProperty("Message", out var mEl) ? mEl.GetString() : null;
                    if (OctopusEventNormalizer.IsNoiseAuditEvent(cat, msg))
                    {
                        continue;
                    }
                    var normalized = OctopusEventNormalizer.NormalizeAuditEventElement(
                        ev,
                        baseUri,
                        projNameMap,
                        envById,
                        fullBackfill ? IngestionChannel.Backfill : IngestionChannel.Poll,
                        now);
                    normalizedEvents.Add(normalized);
                }
            }
            var ingested = await ingestion.IngestBatchAsync(normalizedEvents, ct);

            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.Status = "ok";
            cursor.ItemsProcessed = releaseMap.Count + deliverySnapshots.Count + ingested;
            cursor.LastCursor = now.ToString("O", CultureInfo.InvariantCulture);
            await store.UpsertCursorAsync(cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOctopusSyncFailed(ex);
            cursor.Status = "error";
            cursor.LastError = ex.Message;
            await store.UpsertCursorAsync(cursor, ct);
            throw;
        }
    }

    public async Task<WebhookRegistrationStatus> EnsureSubscriptionRegisteredAsync(string twinPublicBaseUrl, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var endpointUrl = $"{twinPublicBaseUrl.TrimEnd('/')}/hooks/octopus";
        var space = Cmdemo1PlatformCatalog.OctopusSpaceId;

        if (string.IsNullOrWhiteSpace(options.Value.OctopusApiKey) || string.IsNullOrWhiteSpace(options.Value.WebhookSecret))
        {
            return new WebhookRegistrationStatus
            {
                Id = "octopus:subscription",
                Source = "octopus",
                Target = space,
                EndpointUrl = endpointUrl,
                IsActive = false,
                Detail = "OCTOPUS_API_KEY or WEBHOOK_SECRET missing",
                CheckedAt = now
            };
        }

        using var client = CreateClient();

        // Fetch all event categories dynamically so we receive deployments, runbooks, packages, interruptions, machines, etc.
        var catJson = await GetRequiredAsync(client, "/api/events/categories", ct);
        var categories = new List<string>();
        using (var catDoc = JsonDocument.Parse(catJson))
        {
            foreach (var item in catDoc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("Id", out var idEl) && idEl.GetString() is { } cid && !IsNoisyInstanceCategory(cid))
                {
                    categories.Add(cid);
                }
            }
        }

        var existingJson = await GetRequiredAsync(client, $"/api/{space}/subscriptions?take=100", ct);
        string? existingId = null;
        using (var sDoc = JsonDocument.Parse(existingJson))
        {
            foreach (var sub in sDoc.RootElement.GetProperty("Items").EnumerateArray())
            {
                var name = sub.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (string.Equals(name, TwinSubscriptionName, StringComparison.OrdinalIgnoreCase))
                {
                    existingId = sub.GetProperty("Id").GetString();
                    break;
                }
            }
        }

        var payloadObj = new Dictionary<string, object?>
        {
            ["Name"] = TwinSubscriptionName,
            ["Type"] = "Event",
            ["IsDisabled"] = false,
            ["SpaceId"] = space,
            ["EventNotificationSubscription"] = new
            {
                Filter = new
                {
                    Users = Array.Empty<string>(),
                    Projects = Array.Empty<string>(),
                    ProjectGroups = Array.Empty<string>(),
                    Environments = Array.Empty<string>(),
                    EventGroups = Array.Empty<string>(),
                    EventCategories = categories.ToArray(),
                    EventAgents = Array.Empty<string>(),
                    Tenants = Array.Empty<string>(),
                    Tags = Array.Empty<string>(),
                    DocumentTypes = Array.Empty<string>()
                },
                EmailTeams = Array.Empty<string>(),
                EmailFrequencyPeriod = "01:00:00",
                EmailPriority = "Normal",
                EmailShowDatesInTimeZoneId = "UTC",
                WebhookURI = endpointUrl,
                WebhookTeams = Array.Empty<string>(),
                WebhookTimeout = "00:00:10",
                WebhookHeaderKey = "X-Webhook-Secret",
                WebhookHeaderValue = options.Value.WebhookSecret
            }
        };

        if (existingId is not null)
        {
            payloadObj["Id"] = existingId;
        }

        var json = JsonSerializer.Serialize(payloadObj);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = existingId is null
            ? await client.PostAsync($"/api/{space}/subscriptions", content, ct)
            : await client.PutAsync($"/api/{space}/subscriptions/{existingId}", content, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        var subId = existingId;
        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
        {
            using var resDoc = JsonDocument.Parse(body);
            if (resDoc.RootElement.TryGetProperty("Id", out var idEl))
            {
                subId = idEl.GetString();
            }
        }

        var status = new WebhookRegistrationStatus
        {
            Id = $"octopus:{subId ?? TwinSubscriptionName}",
            Source = "octopus",
            Target = space,
            EndpointUrl = endpointUrl,
            IsActive = response.IsSuccessStatusCode,
            ExternalId = subId,
            Detail = response.IsSuccessStatusCode ? $"registered ({categories.Count} categories)" : $"HTTP {(int)response.StatusCode}: {body}",
            CheckedAt = now
        };
        await store.ReplaceWebhookStatusesAsync("octopus", [status], ct);
        return status;
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient("octopus");
        client.BaseAddress = new Uri(options.Value.OctopusUrl.TrimEnd('/'));
        client.DefaultRequestHeaders.Add("X-Octopus-ApiKey", options.Value.OctopusApiKey);
        return client;
    }

    private static async Task<string> GetRequiredAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var resp = await client.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static async Task<string?> GetOptionalAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var resp = await client.GetAsync(path, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
    }

    private static bool IsNoisyInstanceCategory(string categoryId)
        => categoryId.StartsWith("Login", StringComparison.OrdinalIgnoreCase)
        || categoryId.StartsWith("License", StringComparison.OrdinalIgnoreCase)
        || categoryId.StartsWith("UsageDeclaration", StringComparison.OrdinalIgnoreCase)
        || categoryId.StartsWith("ApiKey", StringComparison.OrdinalIgnoreCase)
        || categoryId.StartsWith("Certificate", StringComparison.OrdinalIgnoreCase)
        || categoryId.StartsWith("SigningKey", StringComparison.OrdinalIgnoreCase)
        || categoryId.StartsWith("McpOAuth", StringComparison.OrdinalIgnoreCase);

    private static string? SanitizeUser(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Split('@')[0].Trim();
}
