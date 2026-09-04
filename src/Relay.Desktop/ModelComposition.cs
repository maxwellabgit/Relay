using Relay.Core.Config;
using Relay.Core.Orchestration;
using Relay.Core.Storage;

namespace Relay.Desktop;

/// <summary>Builds the model-backed orchestrator when settings enable it. Filled in by the gateway phase.</summary>
public static class ModelComposition
{
    public static IOrchestrator? Create(RelaySettings settings, DataRoot root) => null;
}
