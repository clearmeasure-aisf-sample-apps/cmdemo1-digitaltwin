using System.Globalization;
using System.Text.RegularExpressions;
using DigitalTwin.Core.BuildAndArtifact;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.ReleaseAndDelivery;
using DigitalTwin.Core.SourceAndChange;
using DigitalTwin.Core.Work;

namespace DigitalTwin.Core.ChangeChains;

public sealed record ChangeChainSources(
    IReadOnlyList<WorkItemSnapshot> WorkItems,
    IReadOnlyList<SourceChangeSnapshot> PullRequests,
    IReadOnlyList<PipelineRunSnapshot> Runs,
    IReadOnlyList<ArtifactPackageSnapshot> Artifacts,
    IReadOnlyList<ReleaseSnapshot> Releases,
    IReadOnlyList<DeliveryExecutionSnapshot> Deliveries);

/// <summary>
/// Links read models into change chains. A chain is anchored on a release candidate (a pipeline's versioned build or
/// Octopus release); open pull requests that have no release candidate yet form chains of their own.
/// </summary>
public static partial class ChangeChainBuilder
{
    private static readonly string[] DeliveryStages = ["tdd", "uat", "prod"];
    private static readonly string[] FailedConclusions = ["failure", "timed_out", "startup_failure"];
    private static readonly TimeSpan OpenPullRequestWindow = TimeSpan.FromDays(14);

    public static IReadOnlyList<ChangeChain> Build(ChangeChainSources sources, DateTimeOffset now)
    {
        var observerPipelines = Cmdemo1PlatformCatalog.Pipelines.Where(p => p.IsTwinObserver).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchors = sources.Releases.Select(r => (r.PipelineId, r.Version))
            .Concat(sources.Runs.Where(r => IsReleaseCandidateVersion(r.Version)).Select(r => (r.PipelineId, Version: r.Version!)))
            .Where(a => !observerPipelines.Contains(a.PipelineId))
            .Distinct()
            .ToList();

        var chains = anchors.Select(a => BuildReleaseCandidateChain(sources, a.PipelineId, a.Version)).ToList();

        var linkedPullRequests = chains.Where(c => c.PullRequestNumber is not null).Select(c => (c.Repository, c.PullRequestNumber)).ToHashSet();
        chains.AddRange(sources.PullRequests
            .Where(p => p.State == "open" && now - p.UpdatedAt <= OpenPullRequestWindow && !observerPipelines.Contains(p.PipelineId))
            .Where(p => !linkedPullRequests.Contains((p.Repository, p.Number)))
            .Select(p => BuildPullRequestChain(sources, p)));

        return chains.OrderByDescending(c => c.LastActivityAt).ToList();
    }

    /// <summary>Chains matching an issue or PR number, version, commit SHA prefix, Octopus release/deployment/task id, or chain id.</summary>
    public static IReadOnlyList<ChangeChain> Find(IEnumerable<ChangeChain> chains, string query)
    {
        var q = query.Trim().TrimStart('#');
        if (q.Length == 0)
        {
            return [];
        }
        var number = int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        var shaPrefix = q.Length >= 7 && q.All(Uri.IsHexDigit) ? q : null;

        return chains.Where(c =>
                c.ChainId.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                (number is not null && (c.WorkItemNumber == number || c.PullRequestNumber == number)) ||
                string.Equals(c.Version, q, StringComparison.OrdinalIgnoreCase) ||
                (shaPrefix is not null && c.CommitSha is not null && c.CommitSha.StartsWith(shaPrefix, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(c.ReleaseId, q, StringComparison.OrdinalIgnoreCase) ||
                c.CorrelationIds.Contains(q, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();
    }

    public static bool IsReleaseCandidateVersion(string? version) => version is not null && ReleaseVersionPattern().IsMatch(version);

    private static ChangeChain BuildReleaseCandidateChain(ChangeChainSources s, string pipelineId, string version)
    {
        var release = s.Releases.Where(r => Same(r.PipelineId, pipelineId) && Same(r.Version, version)).OrderBy(r => r.CreatedAt).LastOrDefault();
        var releaseSha = NullIfEmpty(release?.GitCommitSha);
        var builds = s.Runs.Where(r => Same(r.PipelineId, pipelineId) && Same(r.Version, version)).OrderBy(r => r.UpdatedAt).ToList();
        if (builds.Count == 0 && releaseSha is not null)
        {
            // Some pipelines' build runs carry no version (e.g. cmdemo1-dashboard): link the build workflow by commit.
            builds = s.Runs.Where(r => Same(r.PipelineId, pipelineId) && Same(r.HeadSha, releaseSha) && r.WorkflowName.Contains("build", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.UpdatedAt).ToList();
        }
        var build = builds.LastOrDefault();
        var sha = releaseSha ?? NullIfEmpty(build?.HeadSha);
        var artifact = s.Artifacts
            .Where(a => Same(a.PipelineId, pipelineId) && Same(a.Version, version))
            .OrderBy(a => a.Kind == "OctopusPackage" ? 0 : a.Kind == "ContainerImage" ? 1 : 2)
            .ThenBy(a => a.PublishedAt)
            .FirstOrDefault();
        var deliveries = s.Deliveries.Where(d => Same(d.PipelineId, pipelineId) && Same(d.ReleaseVersion, version)).ToList();
        var repository = build?.Repository;
        var pr = FindPullRequest(s, repository, sha, build);
        repository ??= pr?.Repository;
        var workItem = FindWorkItem(s, repository, pr, build?.Branch ?? pr?.HeadBranch, version);

        var stages = new List<ChainStage>
        {
            WorkStage(workItem, pr),
            PullRequestStage(pr, build),
            BuildStage(builds),
            ReleaseCandidateStage(artifact, release, build, version),
            OctopusReleaseStage(release, artifact, build)
        };
        stages.AddRange(DeliveryStages.Select(stage => DeliveryStage(stage, deliveries.Where(d => Same(d.StageName, stage)).ToList())));

        var deployable = release?.DeployableName ?? deliveries.Select(d => d.DeployableName).FirstOrDefault(d => d is not null)
            ?? Cmdemo1PlatformCatalog.Pipelines.FirstOrDefault(p => Same(p.Id, pipelineId))?.DeployableName;
        var prodDone = stages[^1].Status == ChainStageStatus.Succeeded ? stages[^1].FinishedAt : null;
        var commitAt = release?.CommittedAt ?? build?.CreatedAt;

        var correlation = new List<string?> { sha is null ? null : $"sha:{sha}", release?.ReleaseId }
            .Concat(pr is null ? [] : [$"pr:{pr.Repository}#{pr.Number}", NullIfEmpty(pr.MergeCommitSha) is { } merge ? $"sha:{merge}" : null])
            .Concat(deliveries.SelectMany(d => new[] { d.DeploymentId, d.TaskId }))
            .Concat(builds.Select(b => b.Id));

        return Assemble(
            chainId: $"{deployable ?? pipelineId}@{version}",
            pipelineId, repository, deployable, version, sha,
            title: pr?.Title ?? workItem?.Title ?? build?.DisplayTitle ?? $"{deployable ?? pipelineId} {version}",
            workItem, pr, release?.ReleaseId, stages,
            leadTimeHours: Hours(commitAt, prodDone),
            cycleTimeHours: Hours(workItem?.CreatedAt, prodDone),
            correlation);
    }

    private static ChangeChain BuildPullRequestChain(ChangeChainSources s, SourceChangeSnapshot pr)
    {
        var workItem = FindWorkItem(s, pr.Repository, pr, pr.HeadBranch, version: null);
        var stages = new List<ChainStage> { WorkStage(workItem, pr), PullRequestStage(pr, build: null) };
        stages.AddRange(new[] { ChainStageKind.Build, ChainStageKind.ReleaseCandidate, ChainStageKind.OctopusRelease, ChainStageKind.Tdd, ChainStageKind.Uat, ChainStageKind.Prod }
            .Select(kind => Pending(kind)));
        return Assemble(
            chainId: $"pr:{pr.Repository}#{pr.Number}",
            pr.PipelineId, pr.Repository, deployable: null, version: null, NullIfEmpty(pr.MergeCommitSha),
            pr.Title, workItem, pr, releaseId: null, stages,
            leadTimeHours: null, cycleTimeHours: null,
            [$"pr:{pr.Repository}#{pr.Number}"]);
    }

    private static ChangeChain Assemble(
        string chainId, string pipelineId, string? repository, string? deployable, string? version, string? sha, string title,
        WorkItemSnapshot? workItem, SourceChangeSnapshot? pr, string? releaseId, IReadOnlyList<ChainStage> stages,
        double? leadTimeHours, double? cycleTimeHours, IEnumerable<string?> correlation)
    {
        var started = stages.Where(st => st.Status != ChainStageStatus.Pending && st.Status != ChainStageStatus.Skipped).ToList();
        var current = started.LastOrDefault();
        var failed = stages.Select((st, i) => (st, i))
            .FirstOrDefault(x => x.st.Status == ChainStageStatus.Failed &&
                                 !stages.Skip(x.i + 1).Any(later => later.Status is ChainStageStatus.Succeeded or ChainStageStatus.Running or ChainStageStatus.Waiting)).st;
        var outcome = stages[^1].Status == ChainStageStatus.Succeeded ? ChainOutcome.Delivered
            : failed is not null ? ChainOutcome.Failed
            : stages.Any(st => st.Status == ChainStageStatus.Waiting) ? ChainOutcome.Waiting
            : ChainOutcome.InProgress;
        var times = stages.SelectMany(st => new[] { st.StartedAt, st.FinishedAt }).Where(t => t is not null).Select(t => t!.Value).ToList();

        return new ChangeChain(
            chainId, pipelineId, repository, deployable, version, sha, title,
            workItem?.Number, pr?.Number, releaseId, stages, outcome,
            current?.Kind, failed?.Kind,
            times.Count > 0 ? times.Min() : DateTimeOffset.MinValue,
            times.Count > 0 ? times.Max() : DateTimeOffset.MinValue,
            leadTimeHours, cycleTimeHours,
            correlation.Where(c => !string.IsNullOrEmpty(c)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static SourceChangeSnapshot? FindPullRequest(ChangeChainSources s, string? repository, string? sha, PipelineRunSnapshot? build)
    {
        var candidates = s.PullRequests.Where(p => repository is null || Same(p.Repository, repository)).ToList();
        if (sha is not null)
        {
            var merged = candidates.Where(p => NullIfEmpty(p.MergeCommitSha) is { } m && Same(m, sha)).MaxBy(p => p.UpdatedAt);
            if (merged is not null)
            {
                return merged;
            }
        }
        // Release candidates built from a PR branch before merge.
        return build is null || string.IsNullOrEmpty(build.Branch)
            ? null
            : candidates.Where(p => Same(p.HeadBranch, build.Branch) && p.CreatedAt <= build.UpdatedAt).MaxBy(p => p.CreatedAt);
    }

    private static WorkItemSnapshot? FindWorkItem(ChangeChainSources s, string? repository, SourceChangeSnapshot? pr, string? branch, string? version)
    {
        WorkItemSnapshot? ByNumber(int number)
            => s.WorkItems.FirstOrDefault(w => w.Number == number && (repository is null || Same(w.Repository, repository)))
               ?? (s.WorkItems.Count(w => w.Number == number) == 1 ? s.WorkItems.First(w => w.Number == number) : null);

        if (pr?.LinkedIssueNumber is { } linked && ByNumber(linked) is { } byLink)
        {
            return byLink;
        }
        if (branch is not null && BranchIssuePattern().Match(branch) is { Success: true } m &&
            int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var fromBranch) && ByNumber(fromBranch) is { } byBranch)
        {
            return byBranch;
        }
        if (pr is not null && TitleIssuePattern().Match(pr.Title) is { Success: true } t &&
            int.TryParse(t.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var fromTitle) && ByNumber(fromTitle) is { } byTitle)
        {
            return byTitle;
        }
        return version is null ? null
            : s.WorkItems.FirstOrDefault(w => Same(w.ReleaseCandidateVersion, version) && (repository is null || Same(w.Repository, repository)));
    }

    private static ChainStage WorkStage(WorkItemSnapshot? item, SourceChangeSnapshot? pr)
    {
        if (item is null)
        {
            return new ChainStage(ChainStageKind.WorkItem, ChainStageStatus.Skipped, null, "no linked work item", null, null, null, 0, 0, null);
        }
        var done = pr is not null || item.State == "closed";
        var finished = pr?.CreatedAt ?? item.ClosedAt;
        return new ChainStage(ChainStageKind.WorkItem, done ? ChainStageStatus.Succeeded : ChainStageStatus.Running,
            $"#{item.Number}", item.Column, item.CreatedAt, finished, Minutes(item.CreatedAt, finished), 1, 0, item.Url);
    }

    private static ChainStage PullRequestStage(SourceChangeSnapshot? pr, PipelineRunSnapshot? build)
    {
        if (pr is null)
        {
            return new ChainStage(ChainStageKind.PullRequest, ChainStageStatus.Skipped, build?.Branch, build is null ? null : "direct push", null, null, null, 0, 0, null);
        }
        var status = pr.State switch
        {
            "merged" => ChainStageStatus.Succeeded,
            "closed" => ChainStageStatus.Failed,
            _ => ChainStageStatus.Running
        };
        var detail = pr.State == "closed" ? "closed without merge" : $"{pr.State} · {pr.HeadBranch}";
        return new ChainStage(ChainStageKind.PullRequest, status, $"PR #{pr.Number}", detail, pr.CreatedAt, pr.MergedAt,
            Minutes(pr.CreatedAt, pr.MergedAt), 1, pr.State == "closed" ? 1 : 0, pr.Url);
    }

    private static ChainStage BuildStage(List<PipelineRunSnapshot> builds)
    {
        var build = builds.Count > 0 ? builds[^1] : null;
        if (build is null)
        {
            return Pending(ChainStageKind.Build);
        }
        var status = build.Status != "completed" ? ChainStageStatus.Running
            : build.Conclusion == "success" ? ChainStageStatus.Succeeded
            : FailedConclusions.Contains(build.Conclusion) ? ChainStageStatus.Failed
            : ChainStageStatus.Skipped;
        DateTimeOffset? finished = build.Status == "completed" ? build.UpdatedAt : null;
        return new ChainStage(ChainStageKind.Build, status, build.Version, $"{build.WorkflowName} #{build.RunNumber} · {build.Conclusion ?? build.Status}",
            build.CreatedAt, finished, Minutes(build.CreatedAt, finished), builds.Count,
            builds.Count(b => FailedConclusions.Contains(b.Conclusion)), build.Url);
    }

    private static ChainStage ReleaseCandidateStage(ArtifactPackageSnapshot? artifact, ReleaseSnapshot? release, PipelineRunSnapshot? build, string version)
    {
        DateTimeOffset? buildDone = build?.Status == "completed" ? build.UpdatedAt : null;
        if (artifact is not null)
        {
            return new ChainStage(ChainStageKind.ReleaseCandidate, ChainStageStatus.Succeeded, $"{artifact.Name} {artifact.Version}", artifact.Kind,
                buildDone, artifact.PublishedAt, Minutes(buildDone, artifact.PublishedAt), 1, 0, artifact.Url);
        }
        // An Octopus release implies its package was pushed even when the feed was not observed.
        return release is not null
            ? new ChainStage(ChainStageKind.ReleaseCandidate, ChainStageStatus.Succeeded, version, "package (inferred from release)", null, null, null, 1, 0, null)
            : Pending(ChainStageKind.ReleaseCandidate);
    }

    private static ChainStage OctopusReleaseStage(ReleaseSnapshot? release, ArtifactPackageSnapshot? artifact, PipelineRunSnapshot? build)
    {
        if (release is null)
        {
            return Pending(ChainStageKind.OctopusRelease);
        }
        DateTimeOffset? from = artifact?.PublishedAt ?? (build?.Status == "completed" ? build.UpdatedAt : null);
        return new ChainStage(ChainStageKind.OctopusRelease, ChainStageStatus.Succeeded, release.ReleaseId, $"{release.ProjectSlug} {release.Version} ({release.ChannelName})",
            from, release.CreatedAt, Minutes(from, release.CreatedAt), 1, 0, release.ReleaseUrl);
    }

    private static ChainStage DeliveryStage(string stage, List<DeliveryExecutionSnapshot> attempts)
    {
        var kind = stage switch
        {
            "tdd" => ChainStageKind.Tdd,
            "uat" => ChainStageKind.Uat,
            _ => ChainStageKind.Prod
        };
        var latest = attempts.OrderBy(d => d.QueuedAt).LastOrDefault();
        if (latest is null)
        {
            return Pending(kind);
        }
        var status = latest.HasGuidedFailure || latest.State == "failed" ? ChainStageStatus.Failed
            : latest.HasPendingInterruption || latest.State == "waiting" ? ChainStageStatus.Waiting
            : latest.State == "succeeded" ? ChainStageStatus.Succeeded
            : latest.State == "canceled" ? ChainStageStatus.Skipped
            : ChainStageStatus.Running;
        var started = latest.StartedAt ?? latest.QueuedAt;
        var detail = status == ChainStageStatus.Waiting && latest.WaitsForTitle is not null ? $"waiting: {latest.WaitsForTitle}" : latest.State;
        return new ChainStage(kind, status, latest.DeploymentId, detail, started, latest.FinishedAt, Minutes(started, latest.FinishedAt),
            attempts.Count, attempts.Count(d => d.State == "failed" || d.HasGuidedFailure), latest.TaskUrl);
    }

    private static ChainStage Pending(ChainStageKind kind) => new(kind, ChainStageStatus.Pending, null, null, null, null, null, 0, 0, null);

    private static double? Minutes(DateTimeOffset? from, DateTimeOffset? to)
        => from is null || to is null || to < from ? null : Math.Round((to.Value - from.Value).TotalMinutes, 1);

    private static double? Hours(DateTimeOffset? from, DateTimeOffset? to)
        => from is null || to is null || to < from ? null : Math.Round((to.Value - from.Value).TotalHours, 2);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex ReleaseVersionPattern();

    [GeneratedRegex(@"(?:^|/)(\d+)-")]
    private static partial Regex BranchIssuePattern();

    [GeneratedRegex(@"\(#(\d+)\)")]
    private static partial Regex TitleIssuePattern();
}
