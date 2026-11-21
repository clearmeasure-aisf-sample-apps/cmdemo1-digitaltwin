namespace DigitalTwin.Domain.BuildAndArtifact;

public sealed class PipelineRunSnapshot
{
    public string Id { get; set; } = string.Empty; // "{repo}:{runId}"
    public long RunId { get; set; }
    public long RunNumber { get; set; }
    public string Repository { get; set; } = string.Empty;
    public string PipelineId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // queued | in_progress | completed
    public string? Conclusion { get; set; } // success | failure | cancelled | skipped
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ArtifactPackageSnapshot
{
    public string Id { get; set; } = string.Empty; // "{kind}:{name}:{version}"
    public string Kind { get; set; } = string.Empty; // ContainerImage | OctopusPackage | GitHubRelease
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string RegistryOrFeed { get; set; } = string.Empty;
    public string? CommitSha { get; set; }
    public string PipelineId { get; set; } = string.Empty;
    public string? Url { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}
