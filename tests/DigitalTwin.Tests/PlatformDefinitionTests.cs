using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Application.Services;
using DigitalTwin.Core.ChangeChains;
using DigitalTwin.Core.Hierarchy;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using DigitalTwin.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

public sealed class PlatformDefinitionTests
{
    private static readonly string[] ScannedFolders = ["src", "docs", "infra", "deploy", "scripts", ".github"];
    private static readonly string[] ScannedExtensions = [".md", ".cs", ".js", ".html", ".json", ".bicep", ".tf", ".sh", ".yml"];

    [Theory]
    [InlineData("cmdemo1-workorders", "app-ui", "ui")]
    [InlineData("clearmeasure-aisf-sample-apps/cmdemo1-dashboard", "app-dashboard", "dashboard")]
    [InlineData("cmdemo1-system", "system-gitops", "system")]
    [InlineData("cmdemo1-scorecard", "measurement-scorecard", "scorecard")]
    [InlineData("cmdemo1-aisf", "factory-agentic", "aisf")]
    [InlineData("cmdemo1-video", "factory-agentic", null)]
    [InlineData("cmdemo1-digitaltwin", "twin-observer", null)]
    [InlineData("CMDEMO1-WORKORDERS", "app-ui", "ui")]
    [InlineData("unknown-repo", "system-gitops", null)]
    [InlineData(null, "system-gitops", null)]
    public void TheCmdemo1DefaultResolvesRepositoriesAsTheTwinAlwaysHas(string? repository, string pipeline, string? deployable)
    {
        var cmdemo1 = PlatformDefinition.Cmdemo1();
        Assert.Equal(pipeline, cmdemo1.ResolvePipelineIdFromRepository(repository));
        Assert.Equal(deployable, cmdemo1.ResolveDeployableFromRepository(repository));
    }

    [Theory]
    [InlineData("cmdemo1-ui", "app-ui", "ui")]
    [InlineData("cmdemo1-scorecard-space", "measurement-scorecard", "scorecard")]
    [InlineData("cmdemo1-fworker-runtime", "factory-agentic", "fworker")]
    [InlineData("cmdemo1-digitaltwin", "twin-observer", null)]
    [InlineData("something-else", "system-gitops", null)]
    public void TheCmdemo1DefaultResolvesOctopusProjectsAsTheTwinAlwaysHas(string project, string pipeline, string? deployable)
    {
        var cmdemo1 = PlatformDefinition.Cmdemo1();
        Assert.Equal(pipeline, cmdemo1.ResolvePipelineIdFromOctopusProject(project));
        Assert.Equal(deployable, cmdemo1.ResolveDeployableFromOctopusProject(project));
    }

    [Theory]
    [InlineData("ca-cmdemo1-twin", "rg-cmdemo1-nonprod", "twin")]
    [InlineData("ca-cmdemo1-tdd-ui", "rg-cmdemo1-nonprod", "tdd")]
    [InlineData("sqlcmdemo1uat", "rg-cmdemo1-nonprod", "uat")]
    [InlineData("kv-anything", "rg-cmdemo1-prod", "prod")]
    [InlineData("cae-cmdemo1-pool", "rg-cmdemo1-nonprod", "pool")]
    [InlineData("acrcmdemo1chu4gq", "rg-cmdemo1-nonprod", "shared")]
    public void TheCmdemo1DefaultInfersAzureStagesAsTheTwinAlwaysHas(string name, string resourceGroup, string stage)
        => Assert.Equal(stage, PlatformDefinition.Cmdemo1().InferStageFromAzureResourceName(name, resourceGroup));

    [Fact]
    public void Cmdemo1HasThreeDeliveryStagesAndKeepsItsNames()
    {
        var cmdemo1 = PlatformDefinition.Cmdemo1();
        Assert.Equal(["tdd", "uat", "prod"], cmdemo1.DeliveryStageNames);
        Assert.Equal("cmdemo1-digitaltwin", cmdemo1.TwinServiceName);
        Assert.Equal(["rg-cmdemo1-nonprod", "rg-cmdemo1-prod"], cmdemo1.Azure.ResourceGroups);
    }

    [Fact]
    public void TheCmdemo1PlatformFileIsTheBuiltInDefault()
    {
        var path = Path.Combine(RepoRoot(), "docs", "platforms", "cmdemo1.platform.json");
        var expected = PlatformDefinition.Cmdemo1().ToJson() + "\n";
        if (Environment.GetEnvironmentVariable("TWIN_UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        Assert.Equal(expected, File.ReadAllText(path).ReplaceLineEndings("\n"));
        Assert.Equal(expected, PlatformDefinition.FromJson(expected).ToJson() + "\n");
    }

    [Fact]
    public void AnotherClientsPlatformLoadsWithItsOwnReposProjectsAndStages()
    {
        var contoso = Contoso();
        Assert.Equal("contoso-retail", contoso.GitHub.Org);
        Assert.Equal(["dev", "prod"], contoso.DeliveryStageNames);
        Assert.Equal(("storefront", "web"), (contoso.ResolvePipelineIdFromRepository("contoso-retail/contoso-storefront"), contoso.ResolveDeployableFromRepository("contoso-storefront")));
        Assert.Equal(("api", "api"), (contoso.ResolvePipelineIdFromOctopusProject("Contoso-API"), contoso.ResolveDeployableFromOctopusProject("contoso-api")));
        Assert.Equal("system", contoso.ResolvePipelineIdFromRepository("contoso-docs"));
        Assert.Equal("dev", contoso.InferStageFromAzureResourceName("kv-anything", "rg-contoso-dev"));
        Assert.Equal("contoso-digitaltwin", contoso.TwinServiceName);
    }

    [Fact]
    public void AnInvalidPlatformIsRejectedWithEveryProblem()
    {
        var json = File.ReadAllText(ContosoPath())
            .Replace("\"org\": \"contoso-retail\"", "\"org\": \"\"", StringComparison.Ordinal)
            .Replace("\"contoso-api\": \"api\",\n    \"contoso-digitaltwin\": \"twin-observer\"", "\"contoso-api\": \"orders\",\n    \"contoso-digitaltwin\": \"twin-observer\"", StringComparison.Ordinal)
            .Replace("\"isTwinObserver\": true", "\"isTwinObserver\": false", StringComparison.Ordinal);
        var error = Assert.Throws<InvalidOperationException>(() => PlatformDefinition.FromJson(json));
        Assert.Contains("gitHub.org is required", error.Message, StringComparison.Ordinal);
        Assert.Contains("maps to unknown pipeline 'orders'", error.Message, StringComparison.Ordinal);
        Assert.Contains("one twin observer pipeline", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVocabularyIsPlatformNeverFleet()
    {
        var root = RepoRoot();
        var offenders = ScannedFolders
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "README.md"))
            .Where(f => ScannedExtensions.Contains(Path.GetExtension(f)) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("fleet", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    internal static string ContosoPath() => Path.Combine(RepoRoot(), "docs", "platforms", "contoso.platform.json");

    internal static PlatformDefinition Contoso() => PlatformDefinition.FromJson(File.ReadAllText(ContosoPath()));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cmdemo1-digitaltwin.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

/// <summary>The observed platform is process-wide, so tests that switch it run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlatformIsolation : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "platform-isolation";
}

[Collection(PlatformIsolation.Name)]
public sealed class AnotherPlatformTests(SqlServerFixture sql)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public async Task TheTwinObservesAnotherClientsPlatformFromItsPlatformFile()
    {
        try
        {
            await using var app = new TwinAppFactory(await sql.CreateDatabaseAsync(), new Dictionary<string, string?>
            {
                ["Twin:Platform:File"] = PlatformDefinitionTests.ContosoPath()
            });
            using var client = app.CreateAuthedClient();

            var health = await client.GetFromJsonAsync<JsonElement>("/health");
            Assert.Equal("contoso-digitaltwin", health.GetProperty("service").GetString());

            var summary = await client.GetFromJsonAsync<JsonElement>("/api/twin/summary", Json);
            Assert.Equal("Contoso Retail Platform", summary.GetProperty("platformName").GetString());
            Assert.Equal("contoso", summary.GetProperty("systemSlug").GetString());
            Assert.Equal(["storefront", "api", "system", "twin-observer"], summary.GetProperty("pipelines").EnumerateArray().Select(p => p.GetProperty("definition").GetProperty("id").GetString()));
            Assert.Equal(["dev", "prod"], summary.GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("stage").GetProperty("name").GetString()));

            // A change delivered to dev and then prod fills the chain's first and production slots.
            var state = app.Services.GetRequiredService<StateObservationPublisher>();
            await app.Services.TrackActivity().Timeout(TimeSpan.FromSeconds(60)).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await state.PublishAsync(StateKinds.Release, [F.Release()], EventSourceKind.Octopus, default);
                await state.PublishAsync(StateKinds.Delivery, [F.Delivery("dev", "succeeded", 44, 47), F.Delivery("prod", "succeeded", 60, 63)], EventSourceKind.Octopus, default);
            }));
            var chains = await client.GetFromJsonAsync<JsonElement>("/api/change-chains?limit=10", Json);
            var chain = chains.GetProperty("chains").EnumerateArray().Single();
            var delivery = chain.GetProperty("stages").EnumerateArray().Skip(5).Select(s => (s.GetProperty("kind").GetString(), s.GetProperty("status").GetString())).ToList();
            Assert.Equal([("Tdd", "Succeeded"), ("Prod", "Succeeded")], delivery);
            Assert.Equal("Delivered", chain.GetProperty("outcome").GetString());

            var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("fleet", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            PlatformCatalog.Use(PlatformDefinition.Cmdemo1());
        }
    }

    [Fact]
    public void ANamedPlatformFileThatDoesNotExistStopsStartup()
    {
        try
        {
            using var app = new TwinAppFactory("Server=unused", new Dictionary<string, string?> { ["Twin:Platform:File"] = "/nonexistent/x.platform.json" });
            var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
            Assert.Contains("does not exist", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            PlatformCatalog.Use(PlatformDefinition.Cmdemo1());
        }
    }
}
