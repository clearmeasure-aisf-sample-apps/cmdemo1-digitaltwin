namespace DigitalTwin.Core.ChangeChains;

public enum ChainStageKind
{
    WorkItem,
    PullRequest,
    Build,
    ReleaseCandidate,
    OctopusRelease,
    Tdd,
    Uat,
    Prod
}

public enum ChainStageStatus
{
    Pending,
    Running,
    Waiting,
    Succeeded,
    Failed,
    Skipped
}

public enum ChainOutcome
{
    InProgress,
    Waiting,
    Failed,
    Delivered
}

/// <summary>One hop of a change: what happened, when, and whether it failed (now, or in an earlier attempt).</summary>
public sealed record ChainStage(
    ChainStageKind Kind,
    ChainStageStatus Status,
    string? Reference,
    string? Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    double? DurationMinutes,
    int Attempts,
    int FailedAttempts,
    string? Url);

/// <summary>
/// One change end to end: work item -> branch/PR -> build -> release candidate -> Octopus release -> tdd -> uat -> prod.
/// <see cref="ChainId"/> reuses the journal's correlation keys (<c>sha:&lt;commit&gt;</c>, Octopus release id), so the
/// events that make up a chain can be found by <see cref="CorrelationIds"/>.
/// </summary>
public sealed record ChangeChain(
    string ChainId,
    string PipelineId,
    string? Repository,
    string? Deployable,
    string? Version,
    string? CommitSha,
    string Title,
    int? WorkItemNumber,
    int? PullRequestNumber,
    string? ReleaseId,
    IReadOnlyList<ChainStage> Stages,
    ChainOutcome Outcome,
    ChainStageKind? CurrentStage,
    ChainStageKind? FailedStage,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    double? LeadTimeHours,
    double? CycleTimeHours,
    IReadOnlyList<string> CorrelationIds);
