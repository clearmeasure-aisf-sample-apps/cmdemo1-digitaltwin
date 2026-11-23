using System.Runtime.CompilerServices;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Archive;

/// <summary>Raw intake events, one gzip JSON-lines file per UTC day of capture (<c>raw/yyyy/MM/yyyy-MM-dd.jsonl.gz</c>).</summary>
public sealed class RawIntakeArchive(IArchiveFiles files) : IRawIntakeArchive
{
    public bool IsEnabled => true;

    public string Location => files.Location;

    public async Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct)
        => (await files.ListAsync(RawIntakeArchiveFormat.Prefix, ct)).Select(RawIntakeArchiveFormat.ParseDay).OfType<DateOnly>().Order().ToList();

    public async IAsyncEnumerable<RawIntakeEvent> ReadDayAsync(DateOnly day, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(RawIntakeArchiveFormat.FileName(day), ct);
        if (stream is null)
        {
            yield break;
        }
        await foreach (var ev in RawIntakeArchiveFormat.ReadAsync(stream, ct))
        {
            yield return ev;
        }
    }

    public async Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<RawIntakeEvent> events, CancellationToken ct)
    {
        var count = 0;
        await files.UploadAsync(RawIntakeArchiveFormat.FileName(day), async stream => count = await RawIntakeArchiveFormat.WriteAsync(stream, events, ct), ct);
        return count;
    }
}
