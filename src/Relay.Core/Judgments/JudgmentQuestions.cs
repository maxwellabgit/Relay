using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

/// <summary>Typed judgment request sent through <see cref="IJudgmentClient"/>.</summary>
public sealed class JudgmentRequest
{
    [JsonPropertyName("questionSetId")] public required string QuestionSetId { get; init; }
    [JsonPropertyName("questionSetVersion")] public required string QuestionSetVersion { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("state")] public required JsonElement State { get; init; }
    [JsonPropertyName("questions")] public required IReadOnlyDictionary<string, JudgmentQuestion> Questions { get; init; }
    [JsonPropertyName("sourceObjectRefs")] public IReadOnlyList<JudgmentSourceRef> SourceObjectRefs { get; init; } = [];
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("caseVersion")] public long? CaseVersion { get; init; }
    [JsonPropertyName("disclosureGrantId")] public string? DisclosureGrantId { get; init; }
    [JsonPropertyName("requestHash")] public string? RequestHash { get; init; }
    /// <summary>Provider name used for cache/idempotency hashing (e.g. typesafe, fake).</summary>
    [JsonPropertyName("provider")] public string? Provider { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(QuestionSetId))
            throw new JudgmentValidationException("questionSetId is required.");
        if (string.IsNullOrWhiteSpace(QuestionSetVersion))
            throw new JudgmentValidationException("questionSetVersion is required.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new JudgmentValidationException("model is required.");
        if (Questions is null || Questions.Count == 0)
            throw new JudgmentValidationException("questions must not be empty.");

        foreach (var (id, question) in Questions)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new JudgmentValidationException("question id must not be empty.");
            question.Validate(id);
        }
    }

    /// <summary>
    /// Stable request hash for cache/idempotency. Excludes case id/version, grant id, and timestamps
    /// so the same state in another case can reuse a completed judgment.
    /// </summary>
    public string ComputeRequestHash(string provider) =>
        JudgmentRequestHasher.Compute(
            provider,
            Model,
            QuestionSetId,
            QuestionSetVersion,
            JudgmentRequestHasher.HashQuestions(Questions),
            JudgmentRequestHasher.HashJsonElement(State),
            SourceObjectRefs.Select(r => r.Sha256));
}

public sealed record JudgmentSourceRef(
    [property: JsonPropertyName("objectId")] string ObjectId,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("classification")] string? Classification = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public abstract class JudgmentQuestion
{
    [JsonPropertyName("instructions")] public required string Instructions { get; init; }

    public virtual void Validate(string questionId)
    {
        if (string.IsNullOrWhiteSpace(Instructions))
            throw new JudgmentValidationException($"Question '{questionId}' instructions must contain complete meaning.");
    }
}

public sealed class NoulQuestion : JudgmentQuestion
{
    /// <summary>Optional true/false criteria clarifying what yes and no mean.</summary>
    [JsonPropertyName("criteria")] public IReadOnlyDictionary<string, string>? Criteria { get; init; }
}

public sealed class ChoiceQuestion : JudgmentQuestion
{
    public const string NoMatch = "no_match";

    [JsonPropertyName("criteria")] public required IReadOnlyDictionary<string, string> Criteria { get; init; }

    /// <summary>False only when the criteria provably cover every candidate.</summary>
    [JsonPropertyName("requireNoMatch")] public bool RequireNoMatch { get; init; } = true;

    public override void Validate(string questionId)
    {
        base.Validate(questionId);
        if (Criteria is null || Criteria.Count == 0)
            throw new JudgmentValidationException($"Choice '{questionId}' requires criteria.");
        foreach (var (key, value) in Criteria)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                throw new JudgmentValidationException($"Choice '{questionId}' criteria entries must be non-empty.");
        }

        if (RequireNoMatch && !Criteria.ContainsKey(NoMatch))
            throw new JudgmentValidationException($"Choice '{questionId}' requires a '{NoMatch}' option when coverage may be incomplete.");
    }
}

public sealed class ScoreQuestion : JudgmentQuestion
{
    /// <summary>Ordered semantic levels from lowest to highest.</summary>
    [JsonPropertyName("criteria")] public required IReadOnlyList<string> Criteria { get; init; }

    public override void Validate(string questionId)
    {
        base.Validate(questionId);
        if (Criteria is null || Criteria.Count == 0)
            throw new JudgmentValidationException($"Score '{questionId}' requires ordered criteria.");
        if (Criteria.Any(string.IsNullOrWhiteSpace))
            throw new JudgmentValidationException($"Score '{questionId}' criteria levels must be non-empty.");
    }
}
