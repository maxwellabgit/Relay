using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

/// <summary>
/// Metadata retained for projections/ledger. Raw state and answers live only in the object store.
/// </summary>
public sealed record JudgmentRecord
{
    [JsonPropertyName("judgmentId")] public required string JudgmentId { get; init; }
    [JsonPropertyName("questionSetId")] public required string QuestionSetId { get; init; }
    [JsonPropertyName("questionSetVersion")] public required string QuestionSetVersion { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("caseVersion")] public long? CaseVersion { get; init; }
    [JsonPropertyName("requestObjectId")] public string? RequestObjectId { get; init; }
    [JsonPropertyName("requestHash")] public string? RequestHash { get; init; }
    [JsonPropertyName("responseObjectId")] public string? ResponseObjectId { get; init; }
    [JsonPropertyName("responseHash")] public string? ResponseHash { get; init; }
    [JsonPropertyName("failureCategory")] public string? FailureCategory { get; init; }
    [JsonPropertyName("inputTokens")] public int? InputTokens { get; init; }
    [JsonPropertyName("outputTokens")] public int? OutputTokens { get; init; }
    [JsonPropertyName("elapsedMs")] public long? ElapsedMs { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; init; }
}

public static class JudgmentStatuses
{
    public const string Requested = "requested";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Deferred = "deferred";
}

/// <summary>Stable SHA-256 over the canonical cache/idempotency material.</summary>
public static class JudgmentRequestHasher
{
    public static string Compute(
        string provider,
        string model,
        string questionSetId,
        string questionSetVersion,
        string questionDefinitionsHash,
        string stateHash,
        IEnumerable<string> sourceObjectHashes)
    {
        var sources = string.Join('\n', sourceObjectHashes.OrderBy(h => h, StringComparer.Ordinal));
        var material = string.Join('\n',
            provider.Trim(),
            model.Trim(),
            questionSetId.Trim(),
            questionSetVersion.Trim(),
            questionDefinitionsHash.Trim(),
            stateHash.Trim(),
            sources);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string HashJsonElement(System.Text.Json.JsonElement element)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(element, Storage.RelayJson.Compact);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static string HashQuestions(IReadOnlyDictionary<string, JudgmentQuestion> questions)
    {
        var ordered = questions
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var json = JudgmentJson.Serialize(ordered);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
