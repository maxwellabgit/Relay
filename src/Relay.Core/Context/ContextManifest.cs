using System.Text.Json.Serialization;

namespace Relay.Core.Context;

/// <summary>Record of what entered (or was omitted from) an assembled context package.</summary>
public sealed class ContextManifest
{
    [JsonPropertyName("manifestId")] public required string ManifestId { get; init; }
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("decisionId")] public string? DecisionId { get; init; }
    [JsonPropertyName("includedArtifactIds")] public List<string> IncludedArtifactIds { get; set; } = [];
    [JsonPropertyName("omittedArtifactIds")] public List<string> OmittedArtifactIds { get; set; } = [];
    [JsonPropertyName("truncatedArtifactIds")] public List<string> TruncatedArtifactIds { get; set; } = [];
    [JsonPropertyName("candidateCount")] public int CandidateCount { get; set; }
    [JsonPropertyName("rankedExcerptCount")] public int RankedExcerptCount { get; set; }
    [JsonPropertyName("stateCharCount")] public int StateCharCount { get; set; }
    [JsonPropertyName("splitRequired")] public bool SplitRequired { get; set; }
    [JsonPropertyName("blockReason")] public string? BlockReason { get; set; }
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];
}

public static class ContextBlockReasons
{
    public const string ContextTooLarge = "context_too_large";
    public const string RestrictedExcluded = "restricted_excluded";
}
