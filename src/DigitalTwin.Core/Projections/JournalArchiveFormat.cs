using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Core.Projections;

/// <summary>
/// Journal archive layout: one gzip JSON-lines file per UTC day of occurrence (<c>journal/yyyy/MM/yyyy-MM-dd.jsonl.gz</c>)
/// and, per archived day, a checkpoint holding the newest state observation of every subject at the end of that day
/// (<c>journal-checkpoints/…</c>): the exact input a point-in-time fold needs.
/// </summary>
public static class JournalArchiveFormat
{
    public const string DayPrefix = "journal/";
    public const string CheckpointPrefix = "journal-checkpoints/";
    public const string MetaFile = "journal-meta.json";

    public static string DayFile(DateOnly day) => $"{DayPrefix}{day:yyyy}/{day:MM}/{day:yyyy-MM-dd}.jsonl.gz";

    public static string CheckpointFile(DateOnly day) => $"{CheckpointPrefix}{day:yyyy}/{day:MM}/{day:yyyy-MM-dd}.jsonl.gz";

    public static DateOnly? ParseDay(string name) => RawIntakeArchiveFormat.ParseDay(name);

    public static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    public static async Task<int> WriteAsync(Stream destination, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct)
    {
        var count = 0;
        await using var gzip = new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen: true);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        await foreach (var ev in events.WithCancellation(ct))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(ev, IntakeJson.Options).AsMemory(), ct);
            count++;
        }
        return count;
    }

    public static async IAsyncEnumerable<ObservedEvent> ReadAsync(Stream source, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 0)
            {
                yield return JsonSerializer.Deserialize<ObservedEvent>(line, IntakeJson.Options)
                    ?? throw new InvalidDataException("Empty archived journal event.");
            }
        }
    }

    /// <summary>Reduces state observations to the newest per (kind, subject), with the fold's tie-break, while streaming.</summary>
    public static async Task<List<ObservedEvent>> LatestStatePerSubjectAsync(IAsyncEnumerable<ObservedEvent> events, DateTimeOffset asOf, CancellationToken ct)
    {
        var latest = new Dictionary<(string Kind, string Subject), ObservedEvent>();
        await foreach (var ev in events.WithCancellation(ct))
        {
            if (!StateObservations.IsStateEvent(ev) || ev.OccurredAt > asOf)
            {
                continue;
            }
            var key = (ev.SubjectType, ev.SubjectId.ToUpperInvariant());
            if (!latest.TryGetValue(key, out var current) || StateObservations.IsNewer(ev.OccurredAt, ev.IdempotencyKey, current.OccurredAt, current.IdempotencyKey))
            {
                latest[key] = ev;
            }
        }
        return latest.Values.ToList();
    }
}

/// <summary>Facts about the archived journal that SQL no longer holds once old rows are compacted.</summary>
/// <param name="HistoryStart">The first state observation ever (time travel starts here).</param>
public sealed record JournalArchiveMeta(DateTimeOffset? HistoryStart);

/// <summary>No archive configured: the journal is kept whole in SQL.</summary>
public sealed class DisabledJournalArchive : IJournalArchive
{
    public bool IsEnabled => false;
    public string Location => "disabled";
    public Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DateOnly>>([]);
    public Task<IReadOnlyList<DateOnly>> ListCheckpointsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DateOnly>>([]);
    public IAsyncEnumerable<ObservedEvent> ReadDayAsync(DateOnly day, CancellationToken ct) => AsyncEnumerable.Empty<ObservedEvent>();
    public IAsyncEnumerable<ObservedEvent> ReadCheckpointAsync(DateOnly day, CancellationToken ct) => AsyncEnumerable.Empty<ObservedEvent>();
    public Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct) => throw new InvalidOperationException("No archive configured.");
    public Task<int> WriteCheckpointAsync(DateOnly day, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct) => throw new InvalidOperationException("No archive configured.");
    public Task<JournalArchiveMeta?> ReadMetaAsync(CancellationToken ct) => Task.FromResult<JournalArchiveMeta?>(null);
    public Task WriteMetaAsync(JournalArchiveMeta meta, CancellationToken ct) => throw new InvalidOperationException("No archive configured.");
}
