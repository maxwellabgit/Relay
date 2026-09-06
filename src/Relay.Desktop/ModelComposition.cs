using Relay.Core.Config;
using Relay.Core.Model;
using Relay.Core.Storage;
using Relay.Gateway;
using Relay.Windows;

namespace Relay.Desktop;

/// <summary>
/// Builds model clients for the runtime: RELAY0's own endpoint (loopback http or https) and any
/// external profile (https only). API keys live only in the DPAPI store and are read per request.
/// An invalid endpoint yields no client, so the grammar and the heuristic judge carry on and the UI says so.
/// </summary>
public static class ModelComposition
{
    public static ISecretStore Secrets(DataRoot root) => new DpapiSecretStore(root);

    public static IModelClient? Client(ModelSettings settings, DataRoot root)
    {
        if (!settings.Enabled) return null;
        try
        {
            return new OpenAiCompatibleClient(settings, Secrets(root));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
