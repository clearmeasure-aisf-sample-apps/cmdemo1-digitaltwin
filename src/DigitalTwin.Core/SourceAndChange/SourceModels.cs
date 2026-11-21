namespace DigitalTwin.Core.SourceAndChange;

public sealed class RepositorySnapshot
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public bool IsPrivate { get; set; }
    public bool IsRetired { get; set; }
    public bool IsTwinObserver { get; set; }
    public string PipelineId { get; set; } = string.Empty;
    public string? LatestCommitSha { get; set; }
    public string? LatestCommitMessage { get; set; }
    public string? LatestCommitAuthor { get; set; }
    public DateTimeOffset? LatestCommitAt { get; set; }
    public int OpenPullRequestCount { get; set; }
    public int OpenIssueCount { get; set; }
    public bool WebhookRegistered { get; set; }
    public long? WebhookId { get; set; }
    public int? LastWebhookStatusCode { get; set; }
    public DateTimeOffset? LastWebhookDeliveryAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SourceChangeSnapshot
{
    public string Id { get; set; } = string.Empty; // "{repo}#{number}"
    public string Repository { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string State { get; set; } = "open"; // open | closed | merged
    public string HeadBranch { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public bool IsBot { get; set; }
    public string PipelineId { get; set; } = string.Empty;
    public int? LinkedIssueNumber { get; set; }
    public string? ChecksState { get; set; }
    public string? MergeCommitSha { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
}
