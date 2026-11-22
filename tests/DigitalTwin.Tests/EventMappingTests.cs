using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalTwin.Core.Normalization;
using Xunit;

namespace DigitalTwin.Tests;

/// <summary>
/// The domain event mapping in code (<see cref="SourceEventCatalog"/>) must match what the normalizers do, use only
/// source event names from the official snapshots in docs/sources, and match docs/event-mapping.md.
/// </summary>
public sealed class EventMappingTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-22T00:00:00Z", CultureInfo.InvariantCulture);
    private const string GeneratedStart = "<!-- generated:start (EventMappingTests; TWIN_UPDATE_DOCS=1 regenerates) -->";
    private const string GeneratedEnd = "<!-- generated:end -->";

    public static TheoryData<SourceEventMapping> Rows()
    {
        var data = new TheoryData<SourceEventMapping>();
        foreach (var row in SourceEventCatalog.Mappings.Where(r => r.Source is not SourceSystem.Twin))
        {
            data.Add(row);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryMappingRowIsWhatTheNormalizerProduces(SourceEventMapping row)
    {
        var produced = row.Source switch
        {
            SourceSystem.GitHub => GitHubEventNormalizer.NormalizeWebhook(row.SourceEvent, "map-1", GitHubPayload(row), At).EventType,
            SourceSystem.Octopus => OctopusEventNormalizer.NormalizeSubscriptionPayload(OctopusPayload(row.SourceEvent, row.Condition), null, null, At)!.EventType,
            SourceSystem.AzureEventGrid => Assert.Single(AzureEventNormalizer.NormalizeEventGridWebhook(AzurePayload(row.SourceEvent), At).Events).EventType,
            SourceSystem.AzureResourceGraph when row.Action is not null => AzureEventNormalizer.NormalizeResourceChange(
                "change-1", "/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/ca-cmdemo1-tdd-ui",
                "microsoft.app/containerapps", row.Action, row.Action, "someone", At, At, "{}").EventType,
            SourceSystem.AzureResourceGraph => row.TwinEventType,
            SourceSystem.Twin => row.TwinEventType,
            _ => throw new ArgumentOutOfRangeException(nameof(row), row.Source, "Unknown source")
        };
        Assert.Equal(row.TwinEventType, produced);
    }

    [Fact]
    public void SourceEventNamesMatchTheOfficialDocumentation()
    {
        var github = GitHubDocs();
        foreach (var row in SourceEventCatalog.Mappings.Where(r => r.Source == SourceSystem.GitHub))
        {
            Assert.True(github.ContainsKey(row.SourceEvent), $"GitHub event '{row.SourceEvent}' is not documented");
            if (row.Action is not null)
            {
                Assert.Contains(row.Action, github[row.SourceEvent]);
            }
        }

        var octopus = OctopusCategories();
        Assert.All(SourceEventCatalog.Mappings.Where(r => r.Source == SourceSystem.Octopus), r => Assert.Contains(r.SourceEvent, octopus));

        var azure = AzureTypes();
        Assert.All(SourceEventCatalog.Mappings.Where(r => r.Source == SourceSystem.AzureEventGrid), r => Assert.Contains(r.SourceEvent, azure));
    }

    [Fact]
    public void MappedSourcesHaveNoSilentGaps()
    {
        // Every documented action of a mapped GitHub event has a row: none silently falls through to "updated".
        var github = GitHubDocs();
        foreach (var group in SourceEventCatalog.Mappings.Where(r => r.Source == SourceSystem.GitHub && r.Action is not null).GroupBy(r => r.SourceEvent))
        {
            var missing = github[group.Key].Except(group.Select(r => r.Action!)).ToList();
            Assert.True(missing.Count == 0, $"{group.Key}: unmapped actions {string.Join(", ", missing)}");
        }

        // All nine resource-group event types the Event Grid subscriptions deliver are mapped explicitly.
        Assert.Equal(AzureTypes().Order(), SourceEventCatalog.Mappings.Where(r => r.Source == SourceSystem.AzureEventGrid).Select(r => r.SourceEvent).Order());
    }

    [Fact]
    public void MappingDocumentIsGeneratedFromTheCatalog()
    {
        var path = Path.Combine(RepoRoot(), "docs", "event-mapping.md");
        var current = File.ReadAllText(path);
        var start = current.IndexOf(GeneratedStart, StringComparison.Ordinal);
        var end = current.IndexOf(GeneratedEnd, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "docs/event-mapping.md lacks the generated markers");
        var expected = current[..(start + GeneratedStart.Length)] + "\n" + Render() + current[end..];
        if (Environment.GetEnvironmentVariable("TWIN_UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, expected);
            return;
        }
        Assert.Equal(expected.ReplaceLineEndings("\n"), current.ReplaceLineEndings("\n"));
    }

    private static string Render()
    {
        var sb = new StringBuilder();
        var rows = SourceEventCatalog.Mappings;
        void Line(string text = "") => sb.Append(text).Append('\n');
        string Code(string s) => $"`{s}`";

        Line("### GitHub webhooks (`X-GitHub-Event` + `action`)");
        Line();
        Line("| Source event | Actions / condition | Twin event type | Domain event | Bounded context |");
        Line("|---|---|---|---|---|");
        foreach (var g in rows.Where(r => r.Source == SourceSystem.GitHub).GroupBy(r => (r.SourceEvent, r.Condition, r.TwinEventType, r.DomainEvent, r.BoundedContext)))
        {
            var actions = g.Any(r => r.Action is not null) ? string.Join(", ", g.Select(r => Code(r.Action!))) : "(no action)";
            var qualifier = g.Key.Condition is null ? actions : $"{actions} when {Code(g.Key.Condition)}";
            Line($"| {Code(g.Key.SourceEvent)} | {qualifier} | {Code(g.Key.TwinEventType)} | {g.Key.DomainEvent} | {g.Key.BoundedContext} |");
        }
        Line();

        var groups = OctopusGroups();
        Line("### Octopus audit events (category; event groups from `/api/events/groups`)");
        Line();
        Line("| Category | Event groups | Twin event type | Domain event | Bounded context |");
        Line("|---|---|---|---|---|");
        foreach (var r in rows.Where(r => r.Source == SourceSystem.Octopus))
        {
            var memberOf = groups.Where(kv => kv.Value.Contains(r.SourceEvent)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            var category = r.Condition is null ? Code(r.SourceEvent) : $"{Code(r.SourceEvent)} when {Code(r.Condition)}";
            Line($"| {category} | {(memberOf.Count == 0 ? "-" : string.Join(", ", memberOf))} | {Code(r.TwinEventType)} | {r.DomainEvent} | {r.BoundedContext} |");
        }
        Line();

        Line("### Azure (Event Grid resource-group events, Resource Graph changes) and twin-internal events");
        Line();
        Line("| Source | Source event | Twin event type | Domain event | Bounded context |");
        Line("|---|---|---|---|---|");
        foreach (var r in rows.Where(r => r.Source is SourceSystem.AzureEventGrid or SourceSystem.AzureResourceGraph or SourceSystem.Twin))
        {
            var source = r.Action is null ? Code(r.SourceEvent) : $"{Code(r.SourceEvent)} `changeType = {r.Action}`";
            Line($"| {r.Source} | {source} | {Code(r.TwinEventType)} | {r.DomainEvent} | {r.BoundedContext} |");
        }
        Line();

        Line("### Source events without a domain mapping");
        Line();
        var mappedGitHub = rows.Where(r => r.Source == SourceSystem.GitHub).Select(r => r.SourceEvent).ToHashSet();
        var unmappedGitHub = GitHubDocs().Keys.Where(e => !mappedGitHub.Contains(e)).Order(StringComparer.Ordinal).ToList();
        Line($"**GitHub, {unmappedGitHub.Count} of {GitHubDocs().Count} documented events.** The hooks subscribe to every event, so these are received and journaled under the generic type {Code(SourceEventCatalog.GitHubFallback)}, but no read model or domain rule uses them:");
        Line();
        Line(string.Join(", ", unmappedGitHub.Select(Code)) + ".");
        Line();

        var categories = OctopusCategories();
        var excluded = categories.Where(OctopusEventNormalizer.IsExcludedFromSubscription).ToList();
        var mappedOctopus = rows.Where(r => r.Source == SourceSystem.Octopus).Select(r => r.SourceEvent).ToHashSet();
        var generic = categories.Where(c => !OctopusEventNormalizer.IsExcludedFromSubscription(c) && !mappedOctopus.Contains(c)).ToList();
        Line($"**Octopus, dropped: {excluded.Count} of {categories.Count} categories are never requested.** The subscription excludes instance-level prefixes ({string.Join(", ", OctopusEventNormalizer.SubscriptionExcludedPrefixes.Select(Code))}):");
        Line();
        Line(string.Join(", ", excluded.Select(Code)) + ".");
        Line();
        Line($"**Octopus, generic: {generic.Count} subscribed categories have no domain mapping.** They are journaled as {Code(SourceEventCatalog.OctopusFallback)}:");
        Line();
        Line(string.Join(", ", generic.Select(Code)) + ".");
        Line();
        Line("**Octopus, dropped by message filter:** audit events whose message contains `Ran task Process subscriptions` or `ServerTaskProcessExecution`, and `OIDC token exchanged` events that do not mention cmdemo1 (the twin's own subscription and token traffic).");
        Line();
        Line("**Azure:** none. The Event Grid subscriptions deliver exactly the nine documented resource-group event types, and all nine are mapped.");
        return sb.ToString();
    }

    private static string GitHubPayload(SourceEventMapping row)
    {
        var payload = new JsonObject
        {
            ["repository"] = new JsonObject { ["name"] = "cmdemo1-workorders", ["full_name"] = "clearmeasure-aisf-sample-apps/cmdemo1-workorders" },
            ["sender"] = new JsonObject { ["login"] = "tester" }
        };
        if (row.Action is not null)
        {
            payload["action"] = row.Action;
        }
        if (row.Condition is not null)
        {
            var parts = row.Condition.Split(" = ");
            var path = parts[0].Split('.');
            JsonNode value = parts[1] switch
            {
                "true" => JsonValue.Create(true),
                "false" => JsonValue.Create(false),
                var text => JsonValue.Create(text)
            };
            var node = payload;
            foreach (var segment in path[..^1])
            {
                node = (JsonObject)(node[segment] ??= new JsonObject());
            }
            node[path[^1]] = value;
        }
        return payload.ToJsonString();
    }

    private static string OctopusPayload(string category, string? condition)
    {
        var parts = (condition ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(" = ", 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            Timestamp = "2026-11-22T00:00:00Z",
            EventType = "SubscriptionPayload",
            Payload = new
            {
                ServerUri = "https://clearmeasure.octopus.app",
                Event = new
                {
                    Id = "Events-1",
                    Category = category,
                    Message = parts.GetValueOrDefault("message") ?? $"{category} for cmdemo1-ui",
                    Occurred = "2026-11-22T00:00:00Z",
                    Username = "system",
                    RelatedDocumentIds = parts.TryGetValue("related", out var related) ? new[] { related } : Array.Empty<string>()
                }
            }
        });
    }

    private static string AzurePayload(string eventType)
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "eg-1",
                topic = "/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod",
                subject = "/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/ca-cmdemo1-tdd-ui",
                eventType,
                eventTime = "2026-11-22T00:00:00Z",
                data = new { operationName = "Microsoft.App/containerApps/write", status = "Succeeded", resourceUri = "/subscriptions/x/resourceGroups/rg-cmdemo1-nonprod/providers/Microsoft.App/containerApps/ca-cmdemo1-tdd-ui" },
                dataVersion = "",
                metadataVersion = "1"
            }
        });

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cmdemo1-digitaltwin.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static JsonNode Source(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "sources", file)))!;

    private static Dictionary<string, List<string>> GitHubDocs()
        => Source("github-webhook-events.json")["events"]!.AsObject()
            .ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(a => a!.GetValue<string>()).ToList(), StringComparer.Ordinal);

    private static List<string> OctopusCategories() => Source("octopus-event-categories.json")["categories"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();

    private static Dictionary<string, List<string>> OctopusGroups()
        => Source("octopus-event-categories.json")["groups"]!.AsObject()
            .ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(c => c!.GetValue<string>()).ToList(), StringComparer.Ordinal);

    private static List<string> AzureTypes() => Source("azure-resource-event-types.json")["eventTypes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
}
