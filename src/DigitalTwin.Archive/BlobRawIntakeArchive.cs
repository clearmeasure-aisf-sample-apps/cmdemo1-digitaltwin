using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Archive;

/// <summary>Daily archive files in Azure Blob Storage (cool tier, private container).</summary>
public sealed class BlobRawIntakeArchive(BlobContainerClient container) : IRawIntakeArchive
{
    public bool IsEnabled => true;

    public string Location => container.Uri.ToString();

    public async Task<IReadOnlyList<DateOnly>> ListDaysAsync(CancellationToken ct)
    {
        var days = new List<DateOnly>();
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = RawIntakeArchiveFormat.Prefix }, ct))
        {
            if (RawIntakeArchiveFormat.ParseDay(blob.Name) is { } day)
            {
                days.Add(day);
            }
        }
        return days.Order().ToList();
    }

    public async IAsyncEnumerable<RawIntakeEvent> ReadDayAsync(DateOnly day, [EnumeratorCancellation] CancellationToken ct)
    {
        var blob = container.GetBlobClient(RawIntakeArchiveFormat.FileName(day));
        if (!(await blob.ExistsAsync(ct)).Value)
        {
            yield break;
        }
        await using var stream = await blob.OpenReadAsync(cancellationToken: ct);
        await foreach (var ev in RawIntakeArchiveFormat.ReadAsync(stream, ct))
        {
            yield return ev;
        }
    }

    /// <summary>Spooled to a local temp file first, so the blob is replaced only by a complete upload.</summary>
    public async Task<int> WriteDayAsync(DateOnly day, IAsyncEnumerable<RawIntakeEvent> events, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"twin-archive-{day:yyyy-MM-dd}-{Guid.NewGuid():N}.jsonl.gz");
        try
        {
            int count;
            await using (var file = File.Create(temp))
            {
                count = await RawIntakeArchiveFormat.WriteAsync(file, events, ct);
            }
            await using (var upload = File.OpenRead(temp))
            {
                await container.GetBlobClient(RawIntakeArchiveFormat.FileName(day)).UploadAsync(upload, overwrite: true, ct);
            }
            return count;
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
