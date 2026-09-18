using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Cases;
using Relay.Core.Judgments;

namespace Relay.Core.Decisions;

/// <summary>Production decision dependency — deterministic directives, not model prose.</summary>
public interface ICaseDecisionEngine
{
    Task<CaseDecision> DecideAsync(CaseDecisionRequest request, CancellationToken cancellationToken);
}

public sealed class CaseDecisionRequest
{
    public required string CaseId { get; init; }
    public required string Origin { get; init; }
    public required string Kind { get; init; }
    public string? Objective { get; init; }
    public long Version { get; init; }
    public string Status { get; init; } = CaseStatus.Active;
    public IReadOnlyList<CaseEvent> RecentEvents { get; init; } = [];
    public IReadOnlyList<ListeningSegmentView> RecentSegments { get; init; } = [];
    public IReadOnlyList<OperationEnvelope> PendingOperations { get; init; } = [];
    public IReadOnlyList<string> AvailableCapabilities { get; init; } = [];
    public string? ParentCaseId { get; init; }
    public string? PresentationPolicy { get; init; }
    public string? SessionId { get; init; }
    public string? ProjectId { get; init; }
    public DateTimeOffset At { get; init; }
    public int StepIndex { get; init; }
    public JsonElement? AssembledState { get; init; }
}

public static class CaseDecisionKinds
{
    public const string Wait = "wait";
    public const string PublishFeed = "publish_feed";
    public const string RaiseCase = "raise_case";
    public const string RequestGeneration = "request_generation";
    public const string RequestOperation = "request_operation";
    public const string Complete = "complete";
    public const string NoAction = "no_action";

    public static readonly string[] All =
    [
        Wait, PublishFeed, RaiseCase, RequestGeneration, RequestOperation, Complete, NoAction,
    ];
}

/// <summary>Internal deterministic directive produced by the decision engine.</summary>
public sealed class CaseDecision
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("capabilityId")] public string? CapabilityId { get; init; }
    [JsonPropertyName("capabilityVersion")] public int? CapabilityVersion { get; init; }
    [JsonPropertyName("arguments")] public Dictionary<string, JsonElement> Arguments { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("judgmentIds")] public List<string> JudgmentIds { get; init; } = [];
    [JsonPropertyName("sourceRefs")] public List<string> SourceRefs { get; init; } = [];
    [JsonPropertyName("presentationLevel")] public string? PresentationLevel { get; init; }
    [JsonPropertyName("feedText")] public string FeedText { get; init; } = "";
    [JsonPropertyName("done")] public bool Done { get; init; }
    [JsonPropertyName("childDecisions")] public List<CaseDecision> ChildDecisions { get; init; } = [];
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>Versioned bootstrapping thresholds — not universal truths.</summary>
public sealed class DecisionThresholds
{
    public const string Version = "v1";

    public double DecisionCandidate { get; init; } = 0.80;
    public double CommitmentCandidate { get; init; } = 0.85;
    public double CorrectionCandidate { get; init; } = 0.90;
    public double UnresolvedTermCandidate { get; init; } = 0.75;
    public double AlertConfidence { get; init; } = 0.70;
    public double NegativeCeiling { get; init; } = 0.35;
    public double DirectRouteWinner { get; init; } = 0.55;
    public double DirectRouteConfidence { get; init; } = 0.60;
    public double AcronymConfidence { get; init; } = 0.60;
    public double NoteSupportMin { get; init; } = 0.90;
    public double NoteUnsupportedMax { get; init; } = 0.10;
    public int MaxRaisesPerPass { get; init; } = 2;

    public static DecisionThresholds V1 { get; } = new();
}

/// <summary>Assembles minimal JSON state for a question set — never includes capability/policy instructions from user content.</summary>
public sealed class ContextAssembler
{
    public JsonElement AssembleScreenState(CaseDecisionRequest request, IReadOnlyList<string>? relatedFacts = null)
    {
        var segments = request.RecentSegments.Select(s => new
        {
            segmentId = s.SegmentId,
            speaker = s.Speaker,
            text = s.Text,
            ts = s.Ts,
        }).ToList();

        return JudgmentState.FromObject(new
        {
            origin = request.Origin,
            segments,
            relatedFacts = relatedFacts ?? Array.Empty<string>(),
        });
    }

    public JsonElement AssembleDirectState(CaseDecisionRequest request)
    {
        var text = request.Objective ?? "";
        var lastInput = request.RecentEvents
            .LastOrDefault(e => e.Type == CaseEventTypes.UserInput);
        if (lastInput is not null && lastInput.Payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            text = t.GetString() ?? text;

        return JudgmentState.FromObject(new
        {
            origin = request.Origin,
            text,
            kind = request.Kind,
        });
    }
}
