using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DigitalTwin.Archive;

/// <summary>Where archive files live: a private Blob container in Azure, a directory locally and in tests.</summary>
public interface IArchiveFiles
{
    string Location { get; }
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string name, CancellationToken ct);

    /// <summary>Writes through a temporary copy, so a file is only ever replaced by a complete one.</summary>
    Task UploadAsync(string name, Func<Stream, Task> write, CancellationToken ct);
}

public sealed class BlobArchiveFiles(BlobContainerClient container) : IArchiveFiles
{
    public string Location => container.Uri.ToString();

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, ct))
        {
            names.Add(blob.Name);
        }
        return names;
    }

    public async Task<Stream?> OpenReadAsync(string name, CancellationToken ct)
    {
        var blob = container.GetBlobClient(name);
        return (await blob.ExistsAsync(ct)).Value ? await blob.OpenReadAsync(cancellationToken: ct) : null;
    }

    public async Task UploadAsync(string name, Func<Stream, Task> write, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"twin-archive-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = File.Create(temp))
            {
                await write(file);
            }
            await using var upload = File.OpenRead(temp);
            await container.GetBlobClient(name).UploadAsync(upload, overwrite: true, ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

public sealed class FileSystemArchiveFiles(string root) : IArchiveFiles
{
    public string Location => $"file://{Path.GetFullPath(root)}";

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct)
    {
        var full = Path.GetFullPath(root);
        IReadOnlyList<string> names = Directory.Exists(full)
            ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(full, f).Replace('\\', '/'))
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .ToList()
            : [];
        return Task.FromResult(names);
    }

    public Task<Stream?> OpenReadAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(root, name);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public async Task UploadAsync(string name, Func<Stream, Task> write, CancellationToken ct)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temp))
        {
            await write(stream);
        }
        File.Move(temp, path, overwrite: true);
    }
}
