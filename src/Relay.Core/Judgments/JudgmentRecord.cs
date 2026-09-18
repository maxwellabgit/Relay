using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

/// <summary>
/// Metadata retained for projections/ledger. Raw state and answers live only in the object store.
/// </summary>
public sealed record JudgmentRecord
{
    [JsonPropertyName("judgmentId")] public required string JudgmentId { get; init; }
    [JsonPropertyName("provider")] public string? Provider { get; init; }
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

    public static readonly string[] All = [Requested, Completed, Failed, Deferred];
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
        return ToHex(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static string HashJsonElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }
        return ToHex(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));
    }

    public static string HashQuestions(IReadOnlyDictionary<string, JudgmentQuestion> questions)
    {
        var ordered = new SortedDictionary<string, JudgmentQuestion>(StringComparer.Ordinal);
        foreach (var (key, value) in questions)
            ordered[key] = value;
        var json = JudgmentJson.Serialize(ordered);
        return ToHex(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
