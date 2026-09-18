using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

/// <summary>Immutable, versioned question-set asset. Instructions are never taken from transcript content.</summary>
public sealed class QuestionSetDefinition
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("questions")] public required IReadOnlyDictionary<string, JudgmentQuestion> Questions { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new JudgmentValidationException("question set id is required.");
        if (string.IsNullOrWhiteSpace(Version))
            throw new JudgmentValidationException("question set version is required.");
        if (Questions is null || Questions.Count == 0)
            throw new JudgmentValidationException("question set must define questions.");
        foreach (var (qid, q) in Questions)
            q.Validate(qid);
    }

    public string DefinitionsHash => JudgmentRequestHasher.HashQuestions(Questions);
}

/// <summary>Registry of immutable question-set definitions keyed by id@version.</summary>
public sealed class QuestionSetRegistry
{
    private readonly Dictionary<string, QuestionSetDefinition> _byKey;

    public QuestionSetRegistry(IEnumerable<QuestionSetDefinition> definitions)
    {
        _byKey = new Dictionary<string, QuestionSetDefinition>(StringComparer.Ordinal);
        foreach (var def in definitions)
        {
            def.Validate();
            var key = Key(def.Id, def.Version);
            if (!_byKey.TryAdd(key, def))
                throw new JudgmentValidationException($"Duplicate question set '{key}'.");
        }
    }

    public IReadOnlyDictionary<string, QuestionSetDefinition> All =>
        new ReadOnlyDictionary<string, QuestionSetDefinition>(_byKey);

    public QuestionSetDefinition Get(string id, string version)
    {
        if (!_byKey.TryGetValue(Key(id, version), out var def))
            throw new JudgmentValidationException($"Unknown question set '{id}@{version}'.");
        return def;
    }

    public bool TryGet(string id, string version, out QuestionSetDefinition? definition) =>
        _byKey.TryGetValue(Key(id, version), out definition);

    public static string Key(string id, string version) => $"{id}@{version}";

    /// <summary>Empty registry for early tests; production sets land in later phases.</summary>
    public static QuestionSetRegistry Empty() => new([]);
}

public static class JudgmentState
{
    public static JsonElement FromObject<T>(T value)
    {
        var json = JudgmentJson.Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
