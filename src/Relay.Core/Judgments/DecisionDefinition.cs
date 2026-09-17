using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

/// <summary>Versioned atomic decision definition (stub for §6; full catalog content in §7).</summary>
public sealed class DecisionDefinition
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = "judgment";
    [JsonPropertyName("stateRequirements")] public List<string> StateRequirements { get; init; } = [];
    [JsonPropertyName("questions")] public Dictionary<string, JudgmentQuestion> Questions { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("interpretationNotes")] public string? InterpretationNotes { get; init; }
}
