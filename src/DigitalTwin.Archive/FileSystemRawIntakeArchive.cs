using System.Runtime.CompilerServices;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Archive;

/// <summary>Daily archive files on a local directory (local runs and tests; no Azure needed).</summary>
public sealed class FileSystemRawIntakeArchive(string root) : IRawIntakeArchive
{
    public bool IsEnabled => true;

    public string Location => $"file://{System.IO.Path.GetFullPath(root)}";

    public Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct)
    {
        var dir = System.IO.Path.Combine(root, RawIntakeArchiveFormat.Prefix);
        IReadOnlyList<DateOnly> days = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.jsonl.gz", SearchOption.AllDirectories)
                .Select(f => RawIntakeArchiveFormat.ParseDay(f))
                .Where(d => d is not null)
                .Select(d => d!.Value)
                .Order()
                .ToList()
            : [];
        return Task.FromResult(days);
    }

    public async IAsyncEnumerable<RawIntakeEvent> ReadDayAsync(DateOnly day, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = PathOf(day);
        if (!File.Exists(path))
        {
            yield break;
        }
        await using var stream = File.OpenRead(path);
        await foreach (var ev in RawIntakeArchiveFormat.ReadAsync(stream, ct))
        {
            yield return ev;
        }
    }

    public async Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<RawIntakeEvent> events, CancellationToken ct)
    {
        var path = PathOf(day);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        int count;
        await using (var stream = File.Create(temp))
        {
            count = await RawIntakeArchiveFormat.WriteAsync(stream, events, ct);
        }
        File.Move(temp, path, overwrite: true);
        return count;
    }

    private string PathOf(DateOnly day) => System.IO.Path.Combine(root, RawIntakeArchiveFormat.FileName(day));
}
