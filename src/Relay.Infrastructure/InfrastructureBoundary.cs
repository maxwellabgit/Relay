namespace Relay.Infrastructure;

/// <summary>
/// Marker for the persistence and provider-client project.
/// SQLite repositories and encrypted objects land here; Core stays free of them.
/// </summary>
public static class InfrastructureBoundary
{
    public const string Name = "Relay.Infrastructure";
}
