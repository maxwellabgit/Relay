using System.Text.Json;

namespace Relay.Core.Judgments;

/// <summary>Loads decision definitions from <c>decisions/v1/*.json</c> (or an explicit directory).</summary>
public sealed class DecisionCatalog
{
    private readonly Dictionary<string, DecisionDefinition> _byId = new(StringComparer.Ordinal);

    public DecisionCatalog(IEnumerable<DecisionDefinition> definitions)
    {
        foreach (var d in definitions)
            _byId[d.Id] = d;
    }

    public static DecisionCatalog LoadFromDirectory(string directory)
    {
        var list = new List<DecisionDefinition>();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                var text = File.ReadAllText(file);
                var def = JsonSerializer.Deserialize<DecisionDefinition>(text, JudgmentJson.Options);
                if (def is not null) list.Add(def);
            }
        }
        return new DecisionCatalog(list);
    }

    /// <summary>Resolves <c>decisions/v1</c> relative to the app base or an explicit override.</summary>
    public static DecisionCatalog LoadDefault(string? root = null)
    {
        var baseDir = root ?? AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "decisions", "v1"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "decisions", "v1")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "decisions", "v1")),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && Directory.GetFiles(c, "*.json").Length > 0)
                return LoadFromDirectory(c);
        }
        return new DecisionCatalog([]);
    }

    public IReadOnlyCollection<DecisionDefinition> All => _byId.Values;
    public DecisionDefinition? TryGet(string id) => _byId.GetValueOrDefault(id);
    public int Count => _byId.Count;

    public JudgmentRequest BuildRequest(string decisionId, JsonElement state, string model = JudgmentDefaults.ModelAlias)
    {
        var def = TryGet(decisionId) ?? throw new InvalidOperationException($"Decision '{decisionId}' not found in catalog.");
        return new JudgmentRequest
        {
            Model = model,
            State = state,
            Questions = new Dictionary<string, JudgmentQuestion>(def.Questions, StringComparer.Ordinal),
            RequestId = decisionId,
        };
    }
}
