namespace DigitalTwin.Archive;

public enum ArchiveProvider
{
    None,
    FileSystem,
    AzureBlob
}

/// <summary>Bound from <c>Twin:Archive</c>.</summary>
public sealed class ArchiveOptions
{
    public ArchiveProvider Provider { get; set; } = ArchiveProvider.None;

    /// <summary>Azure Storage connection string (AzureBlob); supplied as a Container App secret, never logged.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string Container { get; set; } = "raw-intake-archive";

    /// <summary>Root directory (FileSystem), used by local runs and tests.</summary>
    public string Path { get; set; } = "raw-intake-archive";
}
