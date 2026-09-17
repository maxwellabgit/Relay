using Relay.Core.Cases;
using Relay.Core.Composition;
using Relay.Core.Config;
using Relay.Core.Model;
using Relay.Core.Search;
using Relay.Core.Storage;
using Relay.Core.Time;
using Relay.Gateway;
using Relay.Windows;

namespace Relay.Desktop;

/// <summary>
/// Builds model and search clients for the runtime. Desktop should compose via
/// <see cref="RelayCompositionFactory"/> for CaseRuntime surfaces; SessionCoordinator
/// remains only until full §12 cutover (see docs/JEV-DECISIONS.md).
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

    /// <summary>Preferred production entry: CaseRuntime surface factory (no SessionCoordinator).</summary>
    public static RelayCompositionOptions CaseRuntimeOptions(DataRoot root, IClock clock, string runId)
        => new()
        {
            Root = root,
            Clock = clock,
            RunId = runId,
            ProviderMode = RelayProviderMode.Production,
            AllowScriptedMinds = false,
            ModelHealth = () => ModelHealthView.Placeholder,
        };
}
