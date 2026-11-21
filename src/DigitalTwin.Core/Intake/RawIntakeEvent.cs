using System.Text.Json;
using DigitalTwin.Core.Observation;

namespace DigitalTwin.Core.Intake;

public enum IntakeKind
{
    GitHubWebhook,
    OctopusWebhook,
    AzureWebhook,
    ObservedBatch
}

/// <summary>
/// Raw, unmodified occurrence captured at the twin's intake boundary before any processing.
/// The raw intake table is the system of record; the event journal is a deterministic projection of it.
/// </summary>
public sealed record RawIntakeEvent
{
    // Signature, secret and auth headers are deliberately excluded so they are never persisted.
    private static readonly string[] CapturedHeaderNames =
    [
        "X-GitHub-Event",
        "X-GitHub-Delivery",
        "X-GitHub-Hook-ID",
        "X-GitHub-Hook-Installation-Target-Type",
        "aeg-event-type",
        "Content-Type",
        "User-Agent"
    ];

    public required Guid Id { get; init; }
    public required EventSourceKind Source { get; init; }
    public required IntakeKind Kind { get; init; }
    public required IngestionChannel Channel { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public required string Body { get; init; }
    public string? DedupeKey { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public int? AppliedCount { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? DeadLetteredAt { get; init; }

    public string? Header(string name)
        => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public static RawIntakeEvent ForWebhook(
        EventSourceKind source,
        IntakeKind kind,
        string body,
        IEnumerable<KeyValuePair<string, string?>> headers,
        DateTimeOffset receivedAt)
    {
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (value is not null && CapturedHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                captured[name] = value;
            }
        }

        return new RawIntakeEvent
        {
            Id = Guid.CreateVersion7(receivedAt),
            Source = source,
            Kind = kind,
            Channel = IngestionChannel.Webhook,
            ReceivedAt = receivedAt,
            Headers = captured,
            Body = body,
            DedupeKey = DeriveDedupeKey(kind, body, captured)
        };
    }

    public static RawIntakeEvent ForObservedBatch(
        IReadOnlyList<ObservedEvent> events,
        EventSourceKind source,
        IngestionChannel channel,
        DateTimeOffset receivedAt)
        => new()
        {
            Id = Guid.CreateVersion7(receivedAt),
            Source = source,
            Kind = IntakeKind.ObservedBatch,
            Channel = channel,
            ReceivedAt = receivedAt,
            Body = JsonSerializer.Serialize(events, IntakeJson.Options)
        };

    private static string? DeriveDedupeKey(IntakeKind kind, string body, Dictionary<string, string> headers)
    {
        switch (kind)
        {
            case IntakeKind.GitHubWebhook:
                return headers.TryGetValue("X-GitHub-Delivery", out var delivery) ? $"github:{delivery}" : null;
            case IntakeKind.OctopusWebhook:
            case IntakeKind.AzureWebhook:
                return TryReadEventId(kind, body);
            case IntakeKind.ObservedBatch:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static string? TryReadEventId(IntakeKind kind, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (kind == IntakeKind.OctopusWebhook &&
                root.TryGetProperty("Payload", out var payload) &&
                payload.TryGetProperty("Event", out var ev) &&
                ev.TryGetProperty(nameof(Id), out var id))
            {
                return $"octopus:{id.GetString()}";
            }
            if (kind == IntakeKind.AzureWebhook && root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
                root[0].TryGetProperty("id", out var azId))
            {
                return $"azure:{azId.GetString()}";
            }
        }
        catch (JsonException)
        {
            // Malformed bodies are still captured verbatim; processing decides what to do with them.
        }
        return null;
    }
}

public sealed record IntakeReceipt(Guid RawEventId, int BodyBytes);

public sealed record IntakeApplyResult(Guid RawEventId, int Normalized, int Applied, bool AlreadyProcessed);

public sealed record RawIntakeStats(
    long Total,
    long Processed,
    int Pending,
    DateTimeOffset? OldestPendingReceivedAt,
    int DeadLettered,
    DateTimeOffset? LastCapturedAt,
    DateTimeOffset? LastProcessedAt);

public sealed record IntakeQueueStatus(
    string Transport,
    bool ProcessorEnabled,
    int Depth,
    DateTimeOffset? OldestPendingReceivedAt,
    double? OldestPendingAgeSeconds,
    int DeadLetterCount,
    long CapturedTotal,
    long ProcessedTotal,
    DateTimeOffset? LastCapturedAt,
    DateTimeOffset? LastProcessedAt,
    int TransportIncoming,
    int TransportScheduled,
    string? Error);

public sealed record DeadLetteredIntake(
    Guid EnvelopeId,
    Guid? RawEventId,
    string MessageType,
    string? ExceptionType,
    string? ExceptionMessage,
    DateTimeOffset? FailedAt,
    bool Replayable);
