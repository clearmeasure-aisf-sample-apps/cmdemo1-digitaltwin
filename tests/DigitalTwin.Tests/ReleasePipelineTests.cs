using System.Text.RegularExpressions;
using DigitalTwin.Core.Hierarchy;
using Xunit;

namespace DigitalTwin.Tests;

/// <summary>The twin's own pipeline as code: the Octopus definitions, their step bootstraps and the shared deploy scripts agree.</summary>
public sealed partial class ReleasePipelineTests
{
    [Fact]
    public void DeploymentsOfTheTwinsOwnOctopusProjectAreAttributedToTheObserverNotTheSystem()
    {
        Assert.Equal("twin-observer", PlatformCatalog.ResolvePipelineIdFromOctopusProject("cmdemo1-digitaltwin"));
        Assert.Null(PlatformCatalog.ResolveDeployableFromOctopusProject("cmdemo1-digitaltwin"));
    }

    [Fact]
    public void EveryOctopusStepRunsAPackagedScriptAndReadsOnlyDefinedVariables()
    {
        var root = RepoRoot();
        var terraform = Path.Combine(root, "deploy", "octopus", "terraform");
        var main = File.ReadAllText(Path.Combine(terraform, "main.tf"));
        var process = File.ReadAllText(Path.Combine(terraform, "process.tf"));
        var packager = File.ReadAllText(Path.Combine(root, "deploy", "scripts", "package-twin.sh"));

        var defined = QuotedKey().Matches(main).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var stepFiles = StepFile().Matches(process).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["deploy.sh", "register-hooks.sh", "verify.sh"], stepFiles.Order(StringComparer.Ordinal));

        foreach (var step in stepFiles)
        {
            var body = File.ReadAllText(Path.Combine(terraform, "steps", step));
            foreach (var variable in OctopusVariable().Matches(body).Select(m => m.Groups[1].Value))
            {
                Assert.True(variable.StartsWith("Octopus.", StringComparison.Ordinal) || defined.Contains(variable), $"{step} reads {variable}, which main.tf does not define");
            }

            foreach (var script in PackagedScript().Matches(body).Select(m => m.Groups[1].Value))
            {
                Assert.True(File.Exists(Path.Combine(root, script)), $"{step} runs {script}, which is not in the repository");
                var folder = Path.GetDirectoryName(script)!.Replace('\\', '/');
                Assert.True(packager.Contains(folder == "scripts" ? "scripts/register-hooks.sh" : "deploy/scripts/*.sh", StringComparison.Ordinal), $"{script} is not copied into the package");
            }
        }

        Assert.Contains("Octopus.Action[Deploy twin].Output.TwinUrl", File.ReadAllText(Path.Combine(terraform, "steps", "verify.sh")), StringComparison.Ordinal);
        Assert.Contains("name  = \"Deploy twin\"", process, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGitHubDeployAndTheOctopusStepsRunTheSameScripts()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci-deploy.yml"));
        Assert.Contains("bash deploy/scripts/deploy-twin.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("bash deploy/scripts/verify-twin.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("bash deploy/scripts/package-twin.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("vars.TWIN_OCTOPUS_RELEASES == 'true'", workflow, StringComparison.Ordinal);
        Assert.Contains("vars.TWIN_DEPLOY_VIA_OCTOPUS != 'true'", workflow, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cmdemo1-digitaltwin.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    [GeneratedRegex("\"((?:Azure|Twin)\\.[A-Za-z]+)\"")]
    private static partial Regex QuotedKey();

    [GeneratedRegex("steps/([a-z-]+\\.sh)")]
    private static partial Regex StepFile();

    [GeneratedRegex("get_octopusvariable \"([^\"]+)\"")]
    private static partial Regex OctopusVariable();

    [GeneratedRegex("\\$PKG/([A-Za-z/.-]+\\.sh)")]
    private static partial Regex PackagedScript();
}
