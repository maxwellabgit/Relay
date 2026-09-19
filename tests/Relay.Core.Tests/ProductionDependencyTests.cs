using System.Reflection;
using System.Xml.Linq;

namespace Relay.Core.Tests;

/// <summary>
/// Distinguishes the temporary legacy solution from the target production boundary.
/// Contracts are defined; the production solution cutover has not happened yet.
/// </summary>
public sealed class ProductionDependencyTests
{
    private static readonly string[] TargetProductionProjects =
    [
        "src/Relay.Core/Relay.Core.csproj",
        "src/Relay.Infrastructure/Relay.Infrastructure.csproj",
        "src/Relay.Connectors/Relay.Connectors.csproj",
        "src/Relay.Desktop/Relay.Desktop.csproj",
    ];

    private static readonly string[] TargetProductionTestProjects =
    [
        "tests/Relay.Core.Tests/Relay.Core.Tests.csproj",
        "tests/Relay.Integration.Tests/Relay.Integration.Tests.csproj",
        "tests/Relay.Desktop.Tests/Relay.Desktop.Tests.csproj",
    ];

    private static readonly string[] TemporaryLegacyProjects =
    [
        "src/Relay.Gateway/Relay.Gateway.csproj",
        "src/Relay.Windows/Relay.Windows.csproj",
        "src/Relay.Worker/Relay.Worker.csproj",
        "src/Relay.DevHarness/Relay.DevHarness.csproj",
        "tests/Relay.Tests/Relay.Tests.csproj",
        "tests/Relay.Gateway.Tests/Relay.Gateway.Tests.csproj",
    ];

    [Fact]
    public void Core_project_does_not_reference_outer_projects()
    {
        var repo = FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(repo, "src", "Relay.Core", "Relay.Core.csproj"));
        foreach (var forbidden in new[] { "Relay.Desktop", "Relay.Windows", "Relay.Connectors", "Relay.Gateway", "Relay.Worker", "Relay.Infrastructure" })
            Assert.DoesNotContain(forbidden, csproj, StringComparison.Ordinal);

        var connectors = File.ReadAllText(Path.Combine(repo, "src", "Relay.Connectors", "Relay.Connectors.csproj"));
        Assert.Contains("Relay.Core", connectors, StringComparison.Ordinal);
        Assert.DoesNotContain("Relay.Desktop", connectors, StringComparison.Ordinal);
        Assert.DoesNotContain("Relay.Infrastructure", connectors, StringComparison.Ordinal);

        var infrastructure = File.ReadAllText(Path.Combine(repo, "src", "Relay.Infrastructure", "Relay.Infrastructure.csproj"));
        Assert.Contains("Relay.Core", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("Relay.Desktop", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("Relay.Connectors", infrastructure, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_no_longer_references_or_copies_Worker()
    {
        var repo = FindRepoRoot();
        var desktop = File.ReadAllText(Path.Combine(repo, "src", "Relay.Desktop", "Relay.Desktop.csproj"));
        Assert.DoesNotContain("Relay.Worker", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkerDlls", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void Temporary_root_solution_still_includes_legacy_projects()
    {
        var repo = FindRepoRoot();
        var paths = ReadSolutionProjectPaths(Path.Combine(repo, "Relay.slnx"));

        foreach (var project in TargetProductionProjects)
            Assert.Contains(project, paths);

        foreach (var project in TemporaryLegacyProjects)
            Assert.Contains(project, paths);

        Assert.Contains("tests/Relay.Core.Tests/Relay.Core.Tests.csproj", paths);
    }

    [Fact]
    public void Target_production_solution_membership_is_documented_and_not_yet_cut_over()
    {
        var repo = FindRepoRoot();
        Assert.False(
            File.Exists(Path.Combine(repo, "Relay.Production.slnx")),
            "Create Relay.Production.slnx (or Relay.Legacy.slnx cutover) only when persistence/runtime compiles.");

        foreach (var project in TargetProductionProjects)
            Assert.True(File.Exists(Path.Combine(repo, project.Replace('/', Path.DirectorySeparatorChar))), project);

        Assert.True(File.Exists(Path.Combine(repo, "tests", "Relay.Core.Tests", "Relay.Core.Tests.csproj")));
        foreach (var missing in TargetProductionTestProjects.Skip(1))
            Assert.False(File.Exists(Path.Combine(repo, missing.Replace('/', Path.DirectorySeparatorChar))), missing);
    }

    [Fact]
    public void Core_assembly_does_not_reference_forbidden_assemblies()
    {
        var core = typeof(Relay.Core.Application.IRelayApplication).Assembly;
        var names = core.GetReferencedAssemblies().Select(a => a.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var forbidden in new[] { "Relay.Desktop", "Relay.Windows", "Relay.Connectors", "Relay.Gateway", "Relay.Worker", "Relay.Infrastructure" })
            Assert.DoesNotContain(forbidden, names);
    }

    [Fact]
    public void Connectors_assembly_references_Core_only_among_Relay_projects()
    {
        var connectors = typeof(Relay.Connectors.ConnectorCatalog).Assembly;
        var relayRefs = connectors.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Relay.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["Relay.Core"], relayRefs);
    }

    private static HashSet<string> ReadSolutionProjectPaths(string slnxPath)
    {
        var doc = XDocument.Load(slnxPath);
        return doc.Descendants("Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Relay.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Relay.slnx not found from test output directory.");
    }
}
