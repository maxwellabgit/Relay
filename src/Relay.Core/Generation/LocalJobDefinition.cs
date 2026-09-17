using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Generation;

public static class LocalJobTypes
{
    public const string ExtractCandidates = "ExtractCandidates";
    public const string ResolveReferenceCandidates = "ResolveReferenceCandidates";
    public const string FormulateSearchQuery = "FormulateSearchQuery";
    public const string FillInterpretiveArguments = "FillInterpretiveArguments";
    public const string DraftAnswer = "DraftAnswer";
    public const string DraftNote = "DraftNote";
    public const string ProposePlan = "ProposePlan";
    public const string DraftImprovementSpecification = "DraftImprovementSpecification";
    public const string DraftCapabilityPackage = "DraftCapabilityPackage";

    public static readonly string[] All =
    [
        ExtractCandidates, ResolveReferenceCandidates, FormulateSearchQuery, FillInterpretiveArguments,
        DraftAnswer, DraftNote, ProposePlan, DraftImprovementSpecification, DraftCapabilityPackage,
    ];
}

/// <summary>Bounded local interpretation/generation job. Cannot invoke tools directly.</summary>
public sealed class LocalJobDefinition
{
    [JsonPropertyName("jobId")] public required string JobId { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "1";
    [JsonPropertyName("caseId")] public required string CaseId { get; init; }
    [JsonPropertyName("objectiveRevision")] public long ObjectiveRevision { get; init; }
    [JsonPropertyName("task")] public required string Task { get; init; }
    [JsonPropertyName("permittedInputArtifactIds")] public List<string> PermittedInputArtifactIds { get; set; } = [];
    [JsonPropertyName("outputSchema")] public string OutputSchema { get; init; } = "json";
    [JsonPropertyName("outputCharLimit")] public int OutputCharLimit { get; init; } = 8000;
    [JsonPropertyName("requireSourceRefs")] public bool RequireSourceRefs { get; init; } = true;
    [JsonPropertyName("logicalDecisionId")] public string? LogicalDecisionId { get; init; }
}

/// <summary>Extracted candidate with exact passage coordinates.</summary>
public sealed class ExtractionCandidate
{
    [JsonPropertyName("candidateId")] public required string CandidateId { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("sourceArtifactId")] public required string SourceArtifactId { get; init; }
    [JsonPropertyName("startOffset")] public required int StartOffset { get; init; }
    [JsonPropertyName("endOffset")] public required int EndOffset { get; init; }
}

public static class ExtractionValidator
{
    public static bool Validate(ExtractionCandidate candidate, string sourceText, out string? error)
    {
        error = null;
        if (candidate.StartOffset < 0 || candidate.EndOffset < candidate.StartOffset)
        {
            error = "invalid_offsets";
            return false;
        }
        if (candidate.EndOffset > sourceText.Length)
        {
            error = "offset_past_end";
            return false;
        }
        var slice = sourceText[candidate.StartOffset..candidate.EndOffset];
        if (!string.Equals(slice, candidate.Text, StringComparison.Ordinal))
        {
            error = "text_mismatch";
            return false;
        }
        if (string.IsNullOrWhiteSpace(candidate.SourceArtifactId) || string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            error = "missing_ids";
            return false;
        }
        return true;
    }
}

/// <summary>Resolution round tracking by logical decision identity. Transport retries do not consume rounds.</summary>
public sealed class ResolutionRoundState
{
    [JsonPropertyName("logicalDecisionId")] public required string LogicalDecisionId { get; init; }
    [JsonPropertyName("round")] public int Round { get; set; }
    // 0 initial; +1 retrieval; +2 reasoning; still unresolved → ask/defer
}

public static class ResolutionRounds
{
    public const int Initial = 0;
    public const int Retrieval = 1;
    public const int Reasoning = 2;

    public static string NextAction(int round) => round switch
    {
        < Retrieval => "retrieval",
        < Reasoning => "reasoning",
        _ => "ask_or_defer",
    };
}
