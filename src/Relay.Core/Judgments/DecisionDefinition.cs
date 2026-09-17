using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

/// <summary>Versioned atomic decision definition.</summary>
public sealed class DecisionDefinition
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = "judgment";
    /// <summary>Primary primitive: noul | choice | score</summary>
    [JsonPropertyName("primitive")] public string Primitive { get; init; } = JudgmentQuestionTypes.Noul;
    [JsonPropertyName("instructions")] public string Instructions { get; init; } = "";
    [JsonPropertyName("criteria")] public JsonElement? Criteria { get; init; }
    [JsonPropertyName("requiredStateSchema")] public List<string> RequiredStateSchema { get; init; } = [];
    [JsonPropertyName("stateRequirements")] public List<string> StateRequirements { get; init; } = [];
    [JsonPropertyName("applicability")] public string Applicability { get; init; } = "when_state_satisfies_schema";
    [JsonPropertyName("interpretationPolicy")] public string InterpretationPolicy { get; init; } = "threshold_v1";
    [JsonPropertyName("uncertainNoFitBehavior")] public string UncertainNoFitBehavior { get; init; } = "return_uncertain";
    [JsonPropertyName("evaluationDatasetVersion")] public string EvaluationDatasetVersion { get; init; } = "eval-v1";
    [JsonPropertyName("questions")] public Dictionary<string, JudgmentQuestion> Questions { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("interpretationNotes")] public string? InterpretationNotes { get; init; }
}
