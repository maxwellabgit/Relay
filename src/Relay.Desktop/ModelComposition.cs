using Relay.Core.Config;
using Relay.Core.Model;
using Relay.Core.Search;
using Relay.Core.Storage;
using Relay.Gateway;
using Relay.Windows;

namespace Relay.Desktop;

/// <summary>
/// Builds model and search clients for the runtime: RELAY0's own endpoint (loopback http or https),
/// any external profile (https only), and the online search provider. API keys live only in the DPAPI
/// store and are read per request. An invalid endpoint yields no client.
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

    public static ISearchClient? Search(SearchSettings settings, DataRoot root)
    {
        if (!settings.Enabled) return null;
        try
        {
            return new HttpSearchClient(settings, Secrets(root));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
