using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Usage;

/// <summary>Kinds of measured improvement Relay may propose after friction evidence.</summary>
public static class ImprovementKinds
{
    public const string Memory = "memory";
    public const string Preference = "preference";
    public const string Workflow = "workflow";
    public const string Tool = "tool";
    public const string HostCapability = "host_capability";
    public const string NoChange = "no_change";

    public static readonly string[] All =
    [
        Memory, Preference, Workflow, Tool, HostCapability, NoChange
    ];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>Friction signal categories captured for personalization.</summary>
public static class FrictionKinds
{
    public const string RepeatedCorrection = "repeated_correction";
    public const string RepeatedToolSequence = "repeated_tool_sequence";
    public const string FailedCapability = "failed_capability";
    public const string RepeatedFiling = "repeated_filing";
    public const string ExcessiveIntervention = "excessive_intervention";

    public static readonly string[] All =
    [
        RepeatedCorrection, RepeatedToolSequence, FailedCapability, RepeatedFiling, ExcessiveIntervention
    ];
}

/// <summary>One friction evidence record (metadata + example refs; no raw user prose required).</summary>
public sealed class FrictionEvidence
{
    [JsonPropertyName("evidenceId")] public required string EvidenceId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("at")] public required DateTimeOffset At { get; init; }
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
    [JsonPropertyName("exampleRefs")] public List<string> ExampleRefs { get; init; } = [];
    [JsonPropertyName("count")] public int Count { get; init; } = 1;
}

/// <summary>One evaluation case attached to an improvement proposal.</summary>
public sealed record ImprovementEvalCase(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("setup")] string Setup,
    [property: JsonPropertyName("expect")] string Expect);

/// <summary>
/// Typed improvement proposal for Slice 7. Must include examples, expected benefit, I/O,
/// permissions, evaluation cases, activation scope, reversion plan, and success metric.
/// </summary>
public sealed class ImprovementProposal
{
    [JsonPropertyName("proposalId")] public required string ProposalId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("examples")] public IReadOnlyList<string> Examples { get; init; } = [];
    [JsonPropertyName("expectedBenefit")] public required string ExpectedBenefit { get; init; }
    [JsonPropertyName("inputs")] public IReadOnlyList<string> Inputs { get; init; } = [];
    [JsonPropertyName("outputs")] public IReadOnlyList<string> Outputs { get; init; } = [];
    [JsonPropertyName("permissions")] public IReadOnlyList<string> Permissions { get; init; } = [];
    [JsonPropertyName("evaluationCases")] public IReadOnlyList<ImprovementEvalCase> EvaluationCases { get; init; } = [];
    [JsonPropertyName("activationScope")] public required string ActivationScope { get; init; }
    [JsonPropertyName("reversionPlan")] public required string ReversionPlan { get; init; }
    [JsonPropertyName("successMetric")] public required string SuccessMetric { get; init; }
    [JsonPropertyName("frictionKind")] public string? FrictionKind { get; init; }
    [JsonPropertyName("evidenceIds")] public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    [JsonPropertyName("draftedAt")] public DateTimeOffset? DraftedAt { get; init; }
    [JsonPropertyName("status")] public string Status { get; set; } = "draft";

    /// <summary>Problems that block promotion; empty when the proposal is complete.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!ImprovementKinds.IsKnown(Kind)) problems.Add($"unknown kind '{Kind}'");
        if (string.IsNullOrWhiteSpace(Title)) problems.Add("title is required");
        if (Kind != ImprovementKinds.NoChange && Examples.Count == 0) problems.Add("examples are required");
        if (string.IsNullOrWhiteSpace(ExpectedBenefit)) problems.Add("expectedBenefit is required");
        if (Kind != ImprovementKinds.NoChange && Inputs.Count == 0) problems.Add("inputs are required");
        if (Kind != ImprovementKinds.NoChange && Outputs.Count == 0) problems.Add("outputs are required");
        if (Kind != ImprovementKinds.NoChange && Permissions.Count == 0) problems.Add("permissions are required");
        if (Kind != ImprovementKinds.NoChange && EvaluationCases.Count == 0) problems.Add("evaluationCases are required");
        if (string.IsNullOrWhiteSpace(ActivationScope)) problems.Add("activationScope is required");
        if (string.IsNullOrWhiteSpace(ReversionPlan)) problems.Add("reversionPlan is required");
        if (string.IsNullOrWhiteSpace(SuccessMetric)) problems.Add("successMetric is required");
        return problems;
    }

    public string ToJson() => JsonSerializer.Serialize(this, RelayJson.Indented);

    public static ImprovementProposal? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ImprovementProposal>(json, RelayJson.Indented); }
        catch (JsonException) { return null; }
    }
}
