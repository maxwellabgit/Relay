using Relay.Core.Connectors;

namespace Relay.Connectors;

/// <summary>
/// Alpha connectors are registered here. Each starts with every write action disabled.
/// Provider SDKs stay in this project, not in Core.
/// </summary>
public static class ConnectorCatalog
{
    public static readonly string[] AlphaConnectors =
    [
        "conversation",
        "memory",
        "google-calendar",
        "gmail",
        "google-sheets",
        "github",
        "public-web",
        "wikipedia",
        "plaid",
    ];

    public static ConnectorDefinition ReadOnly(string id) =>
        new(id, 1, ["read"], []);
}
