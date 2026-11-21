using System.Text.Json;
using DigitalTwin.Domain.Hierarchy;
using DigitalTwin.Domain.Observation;

namespace DigitalTwin.Domain.Normalization;

public static class OctopusEventNormalizer
{
    public static ObservedEvent? NormalizeSubscriptionPayload(
        string rawJson,
        IReadOnlyDictionary<string, string>? projectNamesById,
        IReadOnlyDictionary<string, string>? environmentNamesById,
        DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("Payload", out var payload) && payload.TryGetProperty("Event", out var ev))
        {
            var category = GetString(ev, "Category") ?? string.Empty;
            var message = GetString(ev, "Message") ?? string.Empty;
            if (IsNoiseAuditEvent(category, message))
            {
                return null;
            }
            var serverUri = GetString(payload, "ServerUri")?.TrimEnd('/') ?? Cmdemo1PlatformCatalog.OctopusUrl;
            return NormalizeAuditEventElement(ev, serverUri, projectNamesById, environmentNamesById, IngestionChannel.Webhook, receivedAt, rawJson);
        }

        // Direct event shape fallback
        if (root.TryGetProperty("Category", out _) && root.TryGetProperty("Id", out _))
        {
            var category = GetString(root, "Category") ?? string.Empty;
            var message = GetString(root, "Message") ?? string.Empty;
            if (IsNoiseAuditEvent(category, message))
            {
                return null;
            }
            return NormalizeAuditEventElement(root, Cmdemo1PlatformCatalog.OctopusUrl, projectNamesById, environmentNamesById, IngestionChannel.Webhook, receivedAt, rawJson);
        }

        return null;
    }

    public static bool IsNoiseAuditEvent(string? category, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (message.Contains("Ran task Process subscriptions", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ServerTaskProcessExecution", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (message.Contains("OIDC token exchanged", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("cmdemo1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static ObservedEvent NormalizeAuditEventElement(
        JsonElement ev,
        string serverUri,
        IReadOnlyDictionary<string, string>? projectNamesById,
        IReadOnlyDictionary<string, string>? environmentNamesById,
        IngestionChannel channel,
        DateTimeOffset ingestedAt,
        string? rawPayloadOverride = null)
    {
        var eventId = GetString(ev, "Id") ?? Guid.NewGuid().ToString("N");
        var category = GetString(ev, "Category") ?? "Modified";
        var message = GetString(ev, "Message") ?? category;
        var occurred = ParseDate(GetString(ev, "Occurred")) ?? ingestedAt;
        var actor = GetString(ev, "Username") ?? "system";

        var related = new List<string>();
        if (ev.TryGetProperty("RelatedDocumentIds", out var relEl) && relEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in relEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                {
                    related.Add(s);
                }
            }
        }

        var projectId = related.FirstOrDefault(x => x.StartsWith("Projects-", StringComparison.OrdinalIgnoreCase));
        var envId = related.FirstOrDefault(x => x.StartsWith("Environments-", StringComparison.OrdinalIgnoreCase));
        var taskId = related.FirstOrDefault(x => x.StartsWith("ServerTasks-", StringComparison.OrdinalIgnoreCase));
        var deploymentId = related.FirstOrDefault(x => x.StartsWith("Deployments-", StringComparison.OrdinalIgnoreCase));
        var releaseId = related.FirstOrDefault(x => x.StartsWith("Releases-", StringComparison.OrdinalIgnoreCase));

        var projectName = projectId is not null && projectNamesById is not null && projectNamesById.TryGetValue(projectId, out var pn)
            ? pn
            : InferProjectFromMessage(message);
        var stageName = envId is not null && environmentNamesById is not null && environmentNamesById.TryGetValue(envId, out var en)
            ? en
            : InferStageFromMessage(message);

        var pipelineId = Cmdemo1PlatformCatalog.ResolvePipelineIdFromOctopusProject(projectName);
        var deployable = Cmdemo1PlatformCatalog.ResolveDeployableFromOctopusProject(projectName);

        var (eventType, subjectType, predicate, outcome) = MapCategory(category);
        var subjectId = deploymentId ?? releaseId ?? taskId ?? projectId ?? eventId;
        var sourceUrl = taskId is not null
            ? $"{serverUri}/app#/{Cmdemo1PlatformCatalog.OctopusSpaceId}/tasks/{taskId}"
            : $"{serverUri}/app#/{Cmdemo1PlatformCatalog.OctopusSpaceId}";

        return new ObservedEvent
        {
            EventId = eventId,
            IdempotencyKey = $"octopus:{eventId}",
            Source = EventSourceKind.Octopus,
            SourceEventId = eventId,
            EventType = eventType,
            SubjectType = subjectType,
            SubjectId = subjectId,
            Predicate = predicate,
            OccurredAt = occurred,
            IngestedAt = ingestedAt,
            Channel = channel,
            CorrelationId = deploymentId ?? taskId ?? releaseId,
            ChainId = releaseId ?? deploymentId ?? taskId,
            PipelineId = pipelineId,
            StageName = stageName,
            DeployableName = deployable,
            Actor = actor,
            Outcome = outcome,
            Summary = message,
            SourceUrl = sourceUrl,
            PayloadJson = rawPayloadOverride ?? ev.GetRawText()
        };
    }

    private static (string eventType, string subjectType, string predicate, EventOutcome outcome) MapCategory(string category)
        => category switch
        {
            "DeploymentQueued" => ("twin.octopus.deployment.queued", "service", "queued", EventOutcome.Queued),
            "DeploymentStarted" => ("twin.octopus.deployment.started", "service", "started", EventOutcome.Started),
            "DeploymentSucceeded" => ("dev.cdevents.service.deployed", "service", "deployed", EventOutcome.Success),
            "DeploymentFailed" => ("twin.octopus.deployment.failed", "service", "failed", EventOutcome.Failure),
            "DeploymentCanceled" => ("twin.octopus.deployment.canceled", "service", "canceled", EventOutcome.Canceled),
            "DeploymentResumed" => ("twin.octopus.deployment.resumed", "service", "resumed", EventOutcome.Started),
            "ManualInterventionInterruptionRaised" => ("twin.octopus.interruption.manual", "humanGate", "waiting", EventOutcome.Waiting),
            "GuidedFailureInterruptionRaised" => ("twin.octopus.interruption.guidedFailure", "humanGate", "waiting", EventOutcome.Failure),
            "RunbookRunQueued" => ("twin.octopus.runbook.queued", "taskRun", "queued", EventOutcome.Queued),
            "RunbookRunStarted" => ("twin.octopus.runbook.started", "taskRun", "started", EventOutcome.Started),
            "RunbookRunSucceeded" => ("twin.octopus.runbook.finished", "taskRun", "finished", EventOutcome.Success),
            "RunbookRunFailed" => ("twin.octopus.runbook.finished", "taskRun", "finished", EventOutcome.Failure),
            "PackageAdded" => ("dev.cdevents.artifact.published", "artifact", "published", EventOutcome.Success),
            "BuildInformationCreated" => ("twin.octopus.buildInformation.created", "artifact", "provenance", EventOutcome.Success),
            "EphemeralEnvironmentCreationSucceeded" => ("dev.cdevents.environment.created", "environment", "created", EventOutcome.Success),
            "EphemeralEnvironmentCreationFailed" => ("twin.octopus.ephemeral.failed", "environment", "failed", EventOutcome.Failure),
            _ => ($"twin.octopus.audit.{category}", "octopusDocument", category.ToLowerInvariant(), EventOutcome.None)
        };

    private static string? InferProjectFromMessage(string message)
    {
        foreach (var slug in new[] { "cmdemo1-ui", "cmdemo1-dashboard", "cmdemo1-system", "cmdemo1-scorecard-space", "cmdemo1-scorecard", "cmdemo1-aisf", "cmdemo1-fworker" })
        {
            if (message.Contains(slug, StringComparison.OrdinalIgnoreCase))
            {
                return slug;
            }
        }
        return null;
    }

    private static string? InferStageFromMessage(string message)
    {
        foreach (var stage in new[] { "tdd", "uat", "prod" })
        {
            if (message.Contains($" to {stage}", StringComparison.OrdinalIgnoreCase) ||
                message.Contains($" on {stage}", StringComparison.OrdinalIgnoreCase) ||
                message.Contains($" in {stage}", StringComparison.OrdinalIgnoreCase))
            {
                return stage;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
}
