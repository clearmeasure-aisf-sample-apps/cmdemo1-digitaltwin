using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Core.Intake;

/// <summary>Archive file format: gzip-compressed JSON lines, one raw intake event per line, in capture order.</summary>
public static class RawIntakeArchiveFormat
{
    public const string Prefix = "raw/";

    public static string FileName(DateOnly day) => $"{Prefix}{day:yyyy}/{day:MM}/{day:yyyy-MM-dd}.jsonl.gz";

    public static DateOnly? ParseDay(string name)
    {
        var file = name.Split('/')[^1];
        return file.EndsWith(".jsonl.gz", StringComparison.Ordinal) &&
               DateOnly.TryParseExact(file[..^".jsonl.gz".Length], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    public static async Task<int> WriteAsync(Stream destination, IAsyncEnumerable<RawIntakeEvent> events, CancellationToken ct)
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

    public static async IAsyncEnumerable<RawIntakeEvent> ReadAsync(Stream source, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 0)
            {
                yield return JsonSerializer.Deserialize<RawIntakeEvent>(line, IntakeJson.Options)
                    ?? throw new InvalidDataException("Empty archived raw intake event.");
            }
        }
    }
}

/// <summary>No archive configured: retention never prunes and rebuilds read SQL only.</summary>
public sealed class DisabledRawIntakeArchive : IRawIntakeArchive
{
    public bool IsEnabled => false;
    public string Location => "disabled";
    public Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DateOnly>>([]);
    public IAsyncEnumerable<RawIntakeEvent> ReadDayAsync(DateOnly day, CancellationToken ct) => AsyncEnumerable.Empty<RawIntakeEvent>();
    public Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<RawIntakeEvent> events, CancellationToken ct)
        => throw new InvalidOperationException("No raw intake archive is configured.");
}
