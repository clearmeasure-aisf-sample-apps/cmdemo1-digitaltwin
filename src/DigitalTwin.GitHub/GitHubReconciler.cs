using System.Text.Json;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.GitHub;

/// <summary>
/// Open PRs and recently active issues per repository, GitHub versus the twin (a few REST calls per repository, ETag
/// cached). Repositories where the twin is missing something get a targeted refresh.
/// </summary>
public sealed class GitHubReconciler(GitHubApiClient gh, IGitHubSyncService sync, TimeProvider timeProvider) : ISourceReconciler
{
    private static readonly string[] Repositories = ["cmdemo1-workorders", "cmdemo1-dashboard", "cmdemo1-system", "cmdemo1-scorecard-site"];
    private static readonly TimeSpan IssueWindow = TimeSpan.FromDays(30);

    public string Source => "GitHub";

    public async Task<IReadOnlyList<ReconciliationCheck>> CheckAsync(ITwinReadModel twin, CancellationToken ct)
    {
        var checks = new List<ReconciliationCheck>();
        var prs = await twin.GetSourceChangesAsync(ct);
        var items = await twin.GetWorkItemsAsync(ct);
        var since = timeProvider.GetUtcNow() - IssueWindow;
        foreach (var repo in Repositories)
        {
            var openPrs = await ListAsync($"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repo}/pulls?state=open&per_page=100", _ => true, ct);
            checks.Add(Compare("open pull requests", repo, openPrs,
                prs.Where(p => p.Repository == repo && p.State == "open").Select(p => p.Number)));

            var issues = await ListAsync(
                $"repos/{Cmdemo1PlatformCatalog.GitHubOrg}/{repo}/issues?state=all&per_page=100&since={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
                e => !e.TryGetProperty("pull_request", out _), ct);
            var twinIssues = items.Where(w => w.Repository == repo).Select(w => w.Number).ToHashSet();
            // Only the presence of recently active issues: the twin keeps history beyond the window on purpose.
            checks.Add(issues is null
                ? Failed("recent issues", repo)
                : new ReconciliationCheck(Source, "recent issues", repo, issues.Count(twinIssues.Contains), issues.Count,
                    issues.Where(n => !twinIssues.Contains(n)).Select(n => $"{repo}#{n}").Take(20).ToList(), []));
        }

        foreach (var repo in checks.Where(c => c.MissingInTwin.Count > 0).Select(c => c.Scope).Distinct())
        {
            await HealAsync(() => sync.SyncRepositoryAsync(repo, ct));
        }
        return checks;
    }

    private ReconciliationCheck Compare(string name, string repo, IReadOnlyList<int>? source, IEnumerable<int> twinNumbers)
    {
        if (source is null)
        {
            return Failed(name, repo);
        }
        var twin = twinNumbers.ToHashSet();
        var src = source.ToHashSet();
        return new ReconciliationCheck(Source, name, repo, twin.Count, src.Count,
            src.Where(n => !twin.Contains(n)).Order().Select(n => $"{repo}#{n}").Take(20).ToList(),
            twin.Where(n => !src.Contains(n)).Order().Select(n => $"{repo}#{n}").Take(20).ToList());
    }

    private ReconciliationCheck Failed(string name, string repo) => new(Source, name, repo, 0, 0, [], [], "GitHub request failed or is paused by the rate budget");

    private async Task<IReadOnlyList<int>?> ListAsync(string path, Func<JsonElement, bool> include, CancellationToken ct)
    {
        var res = await gh.GetApiAsync(path, bypassPause: false, ct);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Body))
        {
            return null;
        }
        using var doc = JsonDocument.Parse(res.Body);
        return doc.RootElement.EnumerateArray().Where(include).Select(e => e.GetProperty("number").GetInt32()).ToList();
    }

    /// <summary>Refreshing what drifted is best effort: the comparison itself is the result.</summary>
    private static async Task HealAsync(Func<Task> refresh)
    {
        try
        {
            await refresh();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The next scheduled poll refreshes anyway.
        }
    }
}
