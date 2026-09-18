using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Judgments;

/// <summary>
/// Metadata retained for projections/ledger. Raw state and answers live only in the object store
/// (Phase 4). Included here so request hashing and IDs are stable early.
/// </summary>
public sealed class JudgmentRecord
{
    public required string JudgmentId { get; init; }
    public required string QuestionSetId { get; init; }
    public required string QuestionSetVersion { get; init; }
    public required string Model { get; init; }
    public required string Status { get; init; }
    public string? CaseId { get; init; }
    public long? CaseVersion { get; init; }
    public string? RequestObjectId { get; init; }
    public string? RequestHash { get; init; }
    public string? ResponseObjectId { get; init; }
    public string? ResponseHash { get; init; }
    public string? FailureCategory { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public long? ElapsedMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
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
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string HashJsonElement(JsonElement element)
    {
        var json = JsonSerializer.Serialize(element, RelayJson.Compact);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static string HashQuestions(IReadOnlyDictionary<string, JudgmentQuestion> questions)
    {
        var ordered = questions
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var json = JudgmentJson.Serialize(ordered);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
