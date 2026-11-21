using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalTwin.Core.Normalization;
using DigitalTwin.Core.Observation;

namespace DigitalTwin.Core.Intake;

public static class IntakeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
/// Pure domain service turning a raw intake record into normalized observed events.
/// Output depends only on the raw record, so replaying the raw table reproduces the identical journal.
/// </summary>
public static class IntakeNormalizer
{
    public static IReadOnlyList<ObservedEvent> Normalize(RawIntakeEvent raw)
    {
        switch (raw.Kind)
        {
            case IntakeKind.GitHubWebhook:
            {
                var eventName = raw.Header("X-GitHub-Event") ?? "unknown";
                var deliveryId = raw.Header("X-GitHub-Delivery") ?? raw.Id.ToString("N");
                return [GitHubEventNormalizer.NormalizeWebhook(eventName, deliveryId, raw.Body, raw.ReceivedAt)];
            }
            case IntakeKind.OctopusWebhook:
            {
                var normalized = OctopusEventNormalizer.NormalizeSubscriptionPayload(raw.Body, null, null, raw.ReceivedAt);
                return normalized is null ? [] : [normalized];
            }
            case IntakeKind.AzureWebhook:
                return AzureEventNormalizer.NormalizeEventGridWebhook(raw.Body, raw.ReceivedAt).Events;
            case IntakeKind.ObservedBatch:
                return JsonSerializer.Deserialize<List<ObservedEvent>>(raw.Body, IntakeJson.Options) ?? [];
            default:
                throw new ArgumentOutOfRangeException(nameof(raw), raw.Kind, "Unknown intake kind");
        }
    }
}
