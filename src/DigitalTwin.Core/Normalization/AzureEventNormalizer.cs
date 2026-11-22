using System.Text.Json;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Observation;

namespace DigitalTwin.Core.Normalization;

public sealed record AzureWebhookParseResult(
    string? ValidationResponse,
    IReadOnlyList<ObservedEvent> Events);

public static class AzureEventNormalizer
{
    public static AzureWebhookParseResult NormalizeEventGridWebhook(string rawJson, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "[]" : rawJson);
        var root = doc.RootElement;

        var elements = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.ValueKind == JsonValueKind.Object ? [root] : [];

        var events = new List<ObservedEvent>();
        string? validationCode = null;

        foreach (var item in elements)
        {
            var eventType = GetString(item, "eventType") ?? GetString(item, "type") ?? string.Empty;
            if (eventType.Equals("Microsoft.EventGrid.SubscriptionValidationEvent", StringComparison.OrdinalIgnoreCase))
            {
                validationCode = GetString(item, "data", "validationCode");
                continue;
            }

            var id = GetString(item, "id") ?? Guid.NewGuid().ToString("N");
            var subject = GetString(item, "subject") ?? "";
            var occurred = ParseDate(GetString(item, "eventTime")) ?? ParseDate(GetString(item, "time")) ?? receivedAt;
            var resourceUri = GetString(item, "data", "resourceUri") ?? subject;
            var operationName = GetString(item, "data", "operationName") ?? eventType;
            var correlationId = GetString(item, "data", "correlationId");
            var actor = GetString(item, "data", "claims", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name")
                ?? GetString(item, "data", "claims", "appid")
                ?? "azure-arm";

            var rg = ExtractResourceGroup(resourceUri);
            var resourceName = ExtractResourceName(resourceUri);
            var stage = Cmdemo1PlatformCatalog.InferStageFromAzureResourceName(resourceName, rg);
            var outcome = eventType switch
            {
                var t when t.EndsWith("Success", StringComparison.OrdinalIgnoreCase) => EventOutcome.Success,
                var t when t.EndsWith("Failure", StringComparison.OrdinalIgnoreCase) => EventOutcome.Failure,
                var t when t.EndsWith("Cancel", StringComparison.OrdinalIgnoreCase) => EventOutcome.Canceled,
                _ => EventOutcome.None
            };

            events.Add(new ObservedEvent
            {
                EventId = id,
                IdempotencyKey = $"azure:{id}",
                Source = EventSourceKind.Azure,
                SourceEventId = id,
                EventType = MapEventType(eventType),
                SubjectType = "azureResource",
                SubjectId = string.IsNullOrEmpty(resourceName) ? resourceUri : resourceName,
                Predicate = operationName,
                OccurredAt = occurred,
                IngestedAt = receivedAt,
                Channel = IngestionChannel.Webhook,
                CorrelationId = correlationId,
                ChainId = correlationId,
                PipelineId = resourceName.Contains("ui", StringComparison.OrdinalIgnoreCase) ? "app-ui"
                    : resourceName.Contains("dashboard", StringComparison.OrdinalIgnoreCase) ? "app-dashboard"
                    : resourceName.Contains("twin", StringComparison.OrdinalIgnoreCase) ? "twin-observer"
                    : "system-gitops",
                StageName = stage,
                DeployableName = resourceName.Contains("ui", StringComparison.OrdinalIgnoreCase) ? "ui"
                    : resourceName.Contains("dashboard", StringComparison.OrdinalIgnoreCase) ? "dashboard"
                    : null,
                Actor = actor,
                Outcome = outcome,
                Summary = $"Azure {operationName} on {resourceName} ({rg ?? "sub"})",
                SourceUrl = null,
                PayloadJson = item.GetRawText()
            });
        }

        return new AzureWebhookParseResult(validationCode, events);
    }

    public static ObservedEvent NormalizeResourceChange(
        string changeId,
        string targetResourceId,
        string targetResourceType,
        string changeType,
        string operation,
        string? changedBy,
        DateTimeOffset occurredAt,
        DateTimeOffset ingestedAt,
        string rawJson)
    {
        var rg = ExtractResourceGroup(targetResourceId);
        var resourceName = ExtractResourceName(targetResourceId);
        var stage = Cmdemo1PlatformCatalog.InferStageFromAzureResourceName(resourceName, rg);
        var deployable = resourceName.Contains("-ui", StringComparison.OrdinalIgnoreCase) ? "ui"
            : resourceName.Contains("-dashboard", StringComparison.OrdinalIgnoreCase) ? "dashboard"
            : null;
        var pipelineId = deployable switch
        {
            "ui" => "app-ui",
            "dashboard" => "app-dashboard",
            _ => resourceName.Contains("twin", StringComparison.OrdinalIgnoreCase) ? "twin-observer" : "system-gitops"
        };

        return new ObservedEvent
        {
            EventId = changeId,
            IdempotencyKey = $"azure-arg:{changeId}",
            Source = EventSourceKind.Azure,
            SourceEventId = changeId,
            EventType = $"twin.azure.resource.{changeType.ToLowerInvariant()}",
            SubjectType = targetResourceType,
            SubjectId = resourceName,
            Predicate = operation,
            OccurredAt = occurredAt,
            IngestedAt = ingestedAt,
            Channel = IngestionChannel.Poll,
            CorrelationId = changeId,
            ChainId = changeId,
            PipelineId = pipelineId,
            StageName = stage,
            DeployableName = deployable,
            Actor = changedBy ?? "azure-arm",
            Outcome = EventOutcome.Success,
            Summary = $"Azure {changeType}: {resourceName} ({operation})",
            PayloadJson = rawJson
        };
    }

    private static string MapEventType(string eventType)
        => eventType switch
        {
            "Microsoft.Resources.ResourceWriteSuccess" => "dev.cdevents.environment.modified",
            "Microsoft.Resources.ResourceDeleteSuccess" => "dev.cdevents.environment.deleted",
            "Microsoft.Resources.ResourceWriteFailure" or "Microsoft.Resources.ResourceActionFailure" or "Microsoft.Resources.ResourceDeleteFailure" => "twin.azure.resource.failed",
            "Microsoft.Resources.ResourceActionSuccess" => "twin.azure.resource.action",
            "Microsoft.Resources.ResourceWriteCancel" or "Microsoft.Resources.ResourceDeleteCancel" or "Microsoft.Resources.ResourceActionCancel" => "twin.azure.resource.canceled",
            _ => $"twin.azure.{eventType.ToLowerInvariant()}"
        };

    public static string? ExtractResourceGroup(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }
        var marker = "/resourceGroups/";
        var idx = resourceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }
        var rest = resourceId[(idx + marker.Length)..];
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[..slash] : rest;
    }

    public static string ExtractResourceName(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return string.Empty;
        }
        var trimmed = resourceId.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
}
