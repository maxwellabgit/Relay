namespace Relay.Core.Tests;

/// <summary>
/// Production dependency edges must point inward. Core must not reference Desktop,
/// Windows, connector implementations, or the Gateway HTTP client project.
/// </summary>
public sealed class ProductionDependencyTests
{
    [Fact]
    public void Core_project_does_not_reference_outer_projects()
    {
        var repo = FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(repo, "src", "Relay.Core", "Relay.Core.csproj"));
        foreach (var forbidden in new[] { "Relay.Desktop", "Relay.Windows", "Relay.Connectors", "Relay.Gateway", "Relay.Worker" })
            Assert.DoesNotContain(forbidden, csproj, StringComparison.Ordinal);

        var connectors = File.ReadAllText(Path.Combine(repo, "src", "Relay.Connectors", "Relay.Connectors.csproj"));
        Assert.Contains("Relay.Core", connectors, StringComparison.Ordinal);
        Assert.DoesNotContain("Relay.Desktop", connectors, StringComparison.Ordinal);

        var infrastructure = File.ReadAllText(Path.Combine(repo, "src", "Relay.Infrastructure", "Relay.Infrastructure.csproj"));
        Assert.Contains("Relay.Core", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("Relay.Desktop", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("Relay.Connectors", infrastructure, StringComparison.Ordinal);
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
