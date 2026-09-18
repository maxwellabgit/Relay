using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Cases;

/// <summary>
/// Constrained mind contract for the case runtime. Prefer this over adapting the legacy
/// <c>IMind</c> until the move schemas fully converge; Slice 1 uses a scripted implementation.
/// </summary>
public interface ICaseMind
{
    string Name { get; }
    Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken);
}

public sealed record CaseMindConfidence(
    [property: JsonPropertyName("interpretation")] double Interpretation,
    [property: JsonPropertyName("evidence")] double Evidence,
    [property: JsonPropertyName("utility")] double Utility);

public sealed record CaseMindRead(
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("significance")] double Significance,
    [property: JsonPropertyName("urgency")] double Urgency,
    [property: JsonPropertyName("sensitivity")] double Sensitivity,
    [property: JsonPropertyName("confidence")] CaseMindConfidence Confidence,
    [property: JsonPropertyName("needs")] IReadOnlyList<string> Needs);

public sealed class CaseMove
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("args")] public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("done")] public bool Done { get; init; }

    public const string Say = "say";
    public const string UseTool = "use_tool";
    public const string Propose = "propose";
    public const string Delegate = "delegate";
    public const string Build = "build";
    public const string RunWorkflow = "run_workflow";
    public const string AskUser = "ask_user";
    public const string RaiseTask = "raise_task";
    public const string Wait = "wait";
    public const string Stop = "stop";
}

public sealed record CaseMindStep(
    [property: JsonPropertyName("read")] CaseMindRead? Read,
    [property: JsonPropertyName("move")] CaseMove Move,
    [property: JsonPropertyName("feed")] string Feed);

public sealed record CaseMindRequest(
    string CaseId,
    string Origin,
    string Kind,
    string? Objective,
    long Version,
    string Status,
    IReadOnlyList<CaseEvent> RecentEvents,
    IReadOnlyList<string> PendingOperationIds,
    IReadOnlyList<OperationEnvelope> PendingOperations,
    DateTimeOffset At,
    int StepIndex,
    IReadOnlyList<ListeningSegmentView> RecentSegments,
    string? ParentCaseId = null,
    string? PresentationPolicy = null,
    IReadOnlyList<string>? AvailableTools = null);

/// <summary>
/// Slice 1 scripted mind: first step proposes a side-effecting operation; after that operation
/// is approved/executed it stops. Deterministic for recovery and idempotency tests.
/// </summary>
[Obsolete("Characterization and DevHarness only. Production uses CaseMindDecisionAdapter + RelayDecisionEngine.")]
public sealed class ScriptedCaseMind : ICaseMind
{
    public const string DefaultCapability = "slice1.side_effect";
    public const string DefaultIdempotencyKey = "slice1-side-effect-v1";

    public string Name => "scripted-slice1";

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var pending = request.PendingOperations;
        var awaiting = pending.FirstOrDefault(o => o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing);
        var completed = pending.Any(o => o.Status == OperationStatus.Completed);

        if (completed)
        {
            return Task.FromResult(new CaseMindStep(
                DefaultRead("work complete"),
                new CaseMove { Type = CaseMove.Stop, Text = "done", Done = true },
                "Case complete."));
        }

        if (awaiting is not null)
        {
            return Task.FromResult(new CaseMindStep(
                DefaultRead("waiting for approval or execution"),
                new CaseMove { Type = CaseMove.Wait, Text = "awaiting operation " + awaiting.OperationId },
                "Waiting on operation."));
        }

        // Scope the key to the case so a second harness run on the same data root can still
        // exercise propose→approve. Duplicate completions within one case keep the same key.
        var idempotencyKey = DefaultIdempotencyKey + ":" + request.CaseId;
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capability"] = JsonSerializer.SerializeToElement(DefaultCapability),
            ["capabilityVersion"] = JsonSerializer.SerializeToElement(1),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement(idempotencyKey),
            ["label"] = JsonSerializer.SerializeToElement("slice1-counter"),
        };

        return Task.FromResult(new CaseMindStep(
            DefaultRead("propose side effect"),
            new CaseMove
            {
                Type = CaseMove.Propose,
                Name = DefaultCapability,
                Text = "Run the slice-1 side effect once.",
                Args = args,
            },
            "Proposing side-effect operation."));
    }

    private static CaseMindRead DefaultRead(string intent) => new(
        intent,
        Significance: 0.5,
        Urgency: 0.5,
        Sensitivity: 0.0,
        new CaseMindConfidence(0.9, 0.9, 0.9),
        Needs: []);
}
