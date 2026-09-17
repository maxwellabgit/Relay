using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

public static class JudgmentQuestionTypes
{
    public const string Choice = "choice";
    public const string Score = "score";
    public const string Noul = "noul";
}

/// <summary>One typed question in a System One request.</summary>
public sealed class JudgmentQuestion
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("instructions")] public required string Instructions { get; init; }
    /// <summary>Choice: option map. Score: ordered levels (array). Noul: optional yes/no clarification.</summary>
    [JsonPropertyName("criteria")] public JsonElement? Criteria { get; init; }
}

/// <summary>
/// Outbound System One request: model + state + questions.
/// Serialized to the TypeSafe wire shape for hashing and transport.
/// </summary>
public sealed class JudgmentRequest
{
    [JsonPropertyName("model")] public string Model { get; init; } = JudgmentDefaults.ModelAlias;
    [JsonPropertyName("state")] public required JsonElement State { get; init; }
    [JsonPropertyName("questions")] public required Dictionary<string, JudgmentQuestion> Questions { get; init; }

    /// <summary>Optional correlation for retry persistence / dispatcher.</summary>
    [JsonIgnore] public string? RequestId { get; init; }
}

public static class JudgmentDefaults
{
    public const string ModelAlias = "jev-latest";
    public const string Endpoint = "https://api.typesafe.ai/v1/systemone";
    public const string ApiKeySecretName = "TYPESAFE_API_KEY";
    public const string ApiKeyEnvVar = "TYPESAFE_API_KEY";
    /// <summary>Per-attempt HTTP timeout.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Distributions must sum to 1 within this absolute tolerance.</summary>
    public const double DistributionSumTolerance = 0.01;
}
