using System.Text.Json.Serialization;

namespace Relay.Core.Evidence;

/// <summary>Export restriction on content. <see cref="HostedEligible"/> does not grant permission.</summary>
public static class ContentRestriction
{
    public const string LocalOnly = "local_only";
    public const string HostedEligible = "hosted_eligible";

    public static readonly string[] All = [LocalOnly, HostedEligible];

    /// <summary>Effective restriction is the stricter of the two (local_only wins).</summary>
    public static string Max(string a, string b)
        => a == LocalOnly || b == LocalOnly ? LocalOnly : HostedEligible;

    public static string Max(IEnumerable<string> restrictions)
    {
        var any = false;
        foreach (var r in restrictions)
        {
            any = true;
            if (r == LocalOnly) return LocalOnly;
        }
        return any ? HostedEligible : LocalOnly;
    }
}

/// <summary>
/// Every content artifact carries identity, hash, lineage, and export restriction.
/// <c>hosted_eligible</c> marks export candidacy only — it does not authorize hosted processing.
/// </summary>
public sealed class ContentArtifact
{
    [JsonPropertyName("artifactId")] public required string ArtifactId { get; init; }
    [JsonPropertyName("contentHash")] public required string ContentHash { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "content";
    [JsonPropertyName("sourceRefs")] public List<string> SourceRefs { get; set; } = [];
    [JsonPropertyName("projectIds")] public List<string> ProjectIds { get; set; } = [];
    [JsonPropertyName("sessionIds")] public List<string> SessionIds { get; set; } = [];
    /// <summary>local_only | hosted_eligible</summary>
    [JsonPropertyName("restriction")] public string Restriction { get; set; } = ContentRestriction.LocalOnly;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("inputArtifactIds")] public List<string> InputArtifactIds { get; set; } = [];
    [JsonPropertyName("label")] public string? Label { get; set; }
}

public sealed class EvidenceStatement
{
    [JsonPropertyName("statementId")] public required string StatementId { get; init; }
    [JsonPropertyName("artifactId")] public required string ArtifactId { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("speaker")] public string? Speaker { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class EvidenceConflict
{
    [JsonPropertyName("conflictId")] public required string ConflictId { get; init; }
    [JsonPropertyName("statementIds")] public List<string> StatementIds { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "unresolved";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ProvenanceEdge
{
    [JsonPropertyName("fromArtifactId")] public required string FromArtifactId { get; init; }
    [JsonPropertyName("toArtifactId")] public required string ToArtifactId { get; init; }
    [JsonPropertyName("relation")] public string Relation { get; init; } = "derived_from";
}
