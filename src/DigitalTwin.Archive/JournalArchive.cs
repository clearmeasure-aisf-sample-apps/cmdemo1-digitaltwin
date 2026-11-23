using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Archive;

/// <summary>Journal day files and end-of-day state checkpoints, beside the raw archive in the same container.</summary>
public sealed class JournalArchive(IArchiveFiles files) : IJournalArchive
{
    public bool IsEnabled => true;

    public string Location => files.Location;

    public async Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct)
        => (await files.ListAsync(JournalArchiveFormat.DayPrefix, ct)).Select(JournalArchiveFormat.ParseDay).OfType<DateOnly>().Order().ToList();

    public async Task<IReadOnlyList<DateOnly>> ListCheckpointsAsync(CancellationToken ct)
        => (await files.ListAsync(JournalArchiveFormat.CheckpointPrefix, ct)).Select(JournalArchiveFormat.ParseDay).OfType<DateOnly>().Order().ToList();

    public IAsyncEnumerable<ObservedEvent> ReadDayAsync(DateOnly day, CancellationToken ct) => ReadAsync(JournalArchiveFormat.DayFile(day), ct);

    public IAsyncEnumerable<ObservedEvent> ReadCheckpointAsync(DateOnly day, CancellationToken ct) => ReadAsync(JournalArchiveFormat.CheckpointFile(day), ct);

    public Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct) => WriteAsync(JournalArchiveFormat.DayFile(day), events, ct);

    public Task<int> WriteCheckpointAsync(DateOnly day, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct) => WriteAsync(JournalArchiveFormat.CheckpointFile(day), events, ct);

    public async Task<JournalArchiveMeta?> ReadMetaAsync(CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(JournalArchiveFormat.MetaFile, ct);
        return stream is null ? null : await JsonSerializer.DeserializeAsync<JournalArchiveMeta>(stream, IntakeJson.Options, ct);
    }

    public Task WriteMetaAsync(JournalArchiveMeta meta, CancellationToken ct)
        => files.UploadAsync(JournalArchiveFormat.MetaFile, stream => JsonSerializer.SerializeAsync(stream, meta, IntakeJson.Options, ct), ct);

    private async IAsyncEnumerable<ObservedEvent> ReadAsync(string name, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(name, ct);
        if (stream is null)
        {
            yield break;
        }
        await foreach (var ev in JournalArchiveFormat.ReadAsync(stream, ct))
        {
            yield return ev;
        }
    }

    private async Task<int> WriteAsync(string name, IAsyncEnumerable<ObservedEvent> events, CancellationToken ct)
    {
        var count = 0;
        await files.UploadAsync(name, async stream => count = await JournalArchiveFormat.WriteAsync(stream, events, ct), ct);
        return count;
    }
}
