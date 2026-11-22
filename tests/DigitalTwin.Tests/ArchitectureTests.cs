using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace DigitalTwin.Tests;

/// <summary>
/// Onion architecture guard: dependencies point inward only. Core is pure, Application depends only on Core,
/// each integration project depends only on Core, and only the Api host composes everything.
/// </summary>
public sealed class ArchitectureTests
{
    private const string Core = "DigitalTwin.Core";
    private const string Application = "DigitalTwin.Application";
    private const string Api = "DigitalTwin.Api";

    private static readonly string[] IntegrationProjects =
    [
        "DigitalTwin.GitHub",
        "DigitalTwin.Octopus",
        "DigitalTwin.Azure",
        "DigitalTwin.Probes",
        "DigitalTwin.Persistence",
        "DigitalTwin.Messaging",
        "DigitalTwin.Archive"
    ];

    private static readonly string[] IntegrationAssemblyPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.Http",
        "Azure",
        "Wolverine",
        "WolverineFx",
        "JasperFx",
        "Weasel",
        "Octokit",
        "Octopus",
        "ModelContextProtocol",
        "Testcontainers"
    ];

    [Fact]
    public void CoreHasNoProjectOrPackageReferences()
    {
        var core = LoadProject(Core);
        Assert.Empty(core.ProjectReferences);
        Assert.Empty(core.PackageReferences);
        Assert.Empty(core.FrameworkReferences);
    }

    [Fact]
    public void CoreAssemblyReferencesOnlyTheBaseClassLibrary()
    {
        var offenders = LoadAssembly(Core).GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal) && n is not "netstandard" and not "mscorlib")
            .ToList();
        Assert.True(offenders.Count == 0, $"{Core} must reference only the BCL but references: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ApplicationDependsOnlyOnCoreAndAbstractions()
    {
        var app = LoadProject(Application);
        Assert.Equal([Core], app.ProjectReferences);
        Assert.All(app.PackageReferences, p => Assert.StartsWith("Microsoft.Extensions.", p, StringComparison.Ordinal));
        Assert.Empty(app.FrameworkReferences);
        AssertNoIntegrationAssemblies(Application);
        AssertNoSolutionAssembliesExcept(Application, Core);
    }

    [Theory]
    [MemberData(nameof(IntegrationProjectNames))]
    public void IntegrationProjectsReferenceOnlyCore(string project)
    {
        var proj = LoadProject(project);
        Assert.True(
            proj.ProjectReferences.SequenceEqual([Core]),
            $"{project} may only reference {Core} but references: {string.Join(", ", proj.ProjectReferences)}");
        AssertNoSolutionAssembliesExcept(project, Core);
    }

    [Fact]
    public void OnlyTheApiHostReferencesIntegrationProjects()
    {
        foreach (var dir in Directory.GetDirectories(Path.Combine(RepoRoot, "src")))
        {
            var name = Path.GetFileName(dir);
            if (name == Api)
            {
                continue;
            }
            var refs = LoadProject(name).ProjectReferences;
            var illegal = refs.Intersect(IntegrationProjects.Append(Api)).ToList();
            Assert.True(illegal.Count == 0, $"{name} must not reference {string.Join(", ", illegal)}; only {Api} composes outer projects.");
        }
    }

    [Fact]
    public void ReadModelsCanOnlyChangeThroughStateObservations()
    {
        var writers = typeof(DigitalTwin.Core.Ports.ITwinStore).GetMethods()
            .Select(m => m.Name)
            .Where(n => (n.StartsWith("Upsert", StringComparison.Ordinal) || n.StartsWith("Replace", StringComparison.Ordinal) || n.StartsWith("Reconcile", StringComparison.Ordinal))
                && n != "UpsertCursorAsync")
            .ToList();
        Assert.True(writers.Count == 0, $"ITwinStore must not expose read-model writes (use StateObservationPublisher): {string.Join(", ", writers)}");
    }

    [Fact]
    public void EveryProjectUnderSrcIsClassified()
    {
        var known = IntegrationProjects.Concat([Core, Application, Api]).ToHashSet(StringComparer.Ordinal);
        var unknown = Directory.GetDirectories(Path.Combine(RepoRoot, "src")).Select(Path.GetFileName).Where(n => !known.Contains(n!)).ToList();
        Assert.True(unknown.Count == 0, $"Classify new projects in {nameof(ArchitectureTests)}: {string.Join(", ", unknown)}");
    }

    public static TheoryData<string> IntegrationProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in IntegrationProjects)
        {
            data.Add(p);
        }
        return data;
    }

    private static void AssertNoIntegrationAssemblies(string project)
    {
        var offenders = LoadAssembly(project).GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => IntegrationAssemblyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ToList();
        Assert.True(offenders.Count == 0, $"{project} must not reference integration assemblies: {string.Join(", ", offenders)}");
    }

    private static void AssertNoSolutionAssembliesExcept(string project, params string[] allowed)
    {
        var offenders = LoadAssembly(project).GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("DigitalTwin.", StringComparison.Ordinal) && !allowed.Contains(n))
            .ToList();
        Assert.True(offenders.Count == 0, $"{project} must not reference {string.Join(", ", offenders)}");
    }

    private static Assembly LoadAssembly(string name) => Assembly.Load(new AssemblyName(name));

    private sealed record ProjectInfo(
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<string> PackageReferences,
        IReadOnlyList<string> FrameworkReferences);

    private static ProjectInfo LoadProject(string name)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot, "src", name, $"{name}.csproj"));
        IReadOnlyList<string> Includes(string element) => doc.Descendants(element)
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(i => element == "ProjectReference" ? Path.GetFileNameWithoutExtension(i.Replace('\\', '/')) : i)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new ProjectInfo(Includes("ProjectReference"), Includes("PackageReference"), Includes("FrameworkReference"));
    }

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cmdemo1-digitaltwin.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root (cmdemo1-digitaltwin.sln).");
    }
}
