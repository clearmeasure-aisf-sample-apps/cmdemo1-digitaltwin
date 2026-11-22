using System.Text.Json;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Ports;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Octopus;

/// <summary>
/// Octopus versus the twin: the version currently deployed per project and environment (the Octopus dashboard) and the
/// most recent releases (read-only, two requests). Drift triggers a regular Octopus sync.
/// </summary>
public sealed class OctopusReconciler(IHttpClientFactory httpClientFactory, IOptions<OctopusOptions> options, IOctopusSyncService sync) : ISourceReconciler
{
    private static readonly string[] Stages = ["tdd", "uat", "prod"];

    public string Source => "Octopus";

    public async Task<IReadOnlyList<ReconciliationCheck>> CheckAsync(ITwinReadModel twin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            return [new ReconciliationCheck(Source, "deployed versions", Cmdemo1PlatformCatalog.OctopusSpaceId, 0, 0, [], [], "OCTOPUS_API_KEY not configured")];
        }
        using var client = httpClientFactory.CreateClient("octopus");
        client.BaseAddress = new Uri(options.Value.Url.TrimEnd('/'));
        client.DefaultRequestHeaders.Add("X-Octopus-ApiKey", options.Value.ApiKey);
        var space = Cmdemo1PlatformCatalog.OctopusSpaceId;
        var checks = new List<ReconciliationCheck>
        {
            await DeployedVersionsAsync(client, space, twin, ct),
            await RecentReleasesAsync(client, space, twin, ct)
        };
        if (checks.Any(c => c.MissingInTwin.Count > 0 || c.ExtraInTwin.Count > 0))
        {
            await HealAsync(() => sync.SyncAsync(fullBackfill: false, ct));
        }
        return checks;
    }

    private async Task<ReconciliationCheck> DeployedVersionsAsync(HttpClient client, string space, ITwinReadModel twin, CancellationToken ct)
    {
        const string name = "deployed versions";
        using var resp = await client.GetAsync($"/api/{space}/dashboard", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new ReconciliationCheck(Source, name, space, 0, 0, [], [], $"Octopus dashboard returned {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var projects = doc.RootElement.GetProperty("Projects").EnumerateArray()
            .ToDictionary(p => p.GetProperty("Id").GetString()!, p => (p.TryGetProperty("Slug", out var slug) ? slug.GetString() : null) ?? p.GetProperty("Name").GetString() ?? "", StringComparer.Ordinal);
        var environments = doc.RootElement.GetProperty("Environments").EnumerateArray()
            .ToDictionary(e => e.GetProperty("Id").GetString()!, e => (e.GetProperty("Name").GetString() ?? "").ToLowerInvariant(), StringComparer.Ordinal);

        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
        {
            if (item.TryGetProperty("IsCurrent", out var current) && current.ValueKind == JsonValueKind.True &&
                item.GetProperty("State").GetString() == "Success" &&
                projects.TryGetValue(item.GetProperty("ProjectId").GetString() ?? "", out var project) &&
                environments.TryGetValue(item.GetProperty("EnvironmentId").GetString() ?? "", out var stage) &&
                Stages.Contains(stage) &&
                Cmdemo1PlatformCatalog.ResolveDeployableFromOctopusProject(project) is { } deployable)
            {
                source[$"{deployable}@{stage}"] = item.GetProperty("ReleaseVersion").GetString() ?? "";
            }
        }

        var deliveries = await twin.GetDeliveriesAsync(1000, ct);
        var twinVersions = deliveries.Where(d => d.State == "succeeded" && d.DeployableName is not null && Stages.Contains(d.StageName))
            .GroupBy(d => $"{d.DeployableName}@{d.StageName}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.FinishedAt ?? d.QueuedAt).First().ReleaseVersion, StringComparer.OrdinalIgnoreCase);

        var mismatched = source.Where(kv => !twinVersions.TryGetValue(kv.Key, out var v) || v != kv.Value)
            .Select(kv => $"{kv.Key}: Octopus {kv.Value}, twin {twinVersions.GetValueOrDefault(kv.Key) ?? "none"}")
            .Order(StringComparer.Ordinal)
            .ToList();
        return new ReconciliationCheck(Source, name, space, source.Count - mismatched.Count, source.Count, mismatched, []);
    }

    private async Task<ReconciliationCheck> RecentReleasesAsync(HttpClient client, string space, ITwinReadModel twin, CancellationToken ct)
    {
        const string name = "recent releases";
        using var resp = await client.GetAsync($"/api/{space}/releases?take=20", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new ReconciliationCheck(Source, name, space, 0, 0, [], [], $"Octopus releases returned {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var source = doc.RootElement.GetProperty("Items").EnumerateArray().Select(r => r.GetProperty("Id").GetString()!).ToList();
        var twinIds = (await twin.GetReleasesAsync(1000, ct)).Select(r => r.ReleaseId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = source.Where(id => !twinIds.Contains(id)).ToList();
        return new ReconciliationCheck(Source, name, space, source.Count - missing.Count, source.Count, missing, []);
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
