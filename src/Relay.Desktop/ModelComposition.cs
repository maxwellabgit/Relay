using Relay.Core.Config;
using Relay.Core.Model;
using Relay.Core.Orchestration;
using Relay.Core.Storage;
using Relay.Gateway;
using Relay.Windows;

namespace Relay.Desktop;

/// <summary>
/// Builds the model-backed orchestrator when settings enable it. The API key lives only in the
/// DPAPI store; the client reads it per request. When the endpoint is invalid the model is simply
/// absent and the rules orchestrator carries the turn.
/// </summary>
public static class ModelComposition
{
    public static ISecretStore Secrets(DataRoot root) => new DpapiSecretStore(root);

    public static IOrchestrator? Create(RelaySettings settings, DataRoot root)
    {
        if (!settings.Model.Enabled) return null;
        try
        {
            var client = new OpenAiCompatibleClient(settings.Model, Secrets(root));
            return new ModelOrchestrator(client, settings.Model.MaxOutputTokens);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
