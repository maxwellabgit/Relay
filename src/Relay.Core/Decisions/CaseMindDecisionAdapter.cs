using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Policy;

namespace Relay.Core.Decisions;

/// <summary>
/// Temporary adapter: exposes <see cref="ICaseDecisionEngine"/> as <see cref="ICaseMind"/>
/// so CaseRuntime can migrate without a big-bang cutover. Delete after CaseRuntime takes
/// ICaseDecisionEngine directly.
/// </summary>
public sealed class CaseMindDecisionAdapter : ICaseMind
{
    private readonly ICaseDecisionEngine _engine;
    private readonly string? _sessionId;
    private readonly string? _projectId;

    public CaseMindDecisionAdapter(
        ICaseDecisionEngine engine,
        string? sessionId = null,
        string? projectId = null)
    {
        _engine = engine;
        _sessionId = sessionId;
        _projectId = projectId;
    }

    public string Name => "decision-engine";

    public async Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var decisionRequest = new CaseDecisionRequest
        {
            CaseId = request.CaseId,
            Origin = request.Origin,
            Kind = request.Kind,
            Objective = request.Objective,
            Version = request.Version,
            Status = request.Status,
            RecentEvents = request.RecentEvents,
            RecentSegments = request.RecentSegments,
            PendingOperations = request.PendingOperations,
            AvailableCapabilities = request.AvailableTools ?? [],
            ParentCaseId = request.ParentCaseId,
            PresentationPolicy = request.PresentationPolicy,
            At = request.At,
            StepIndex = request.StepIndex,
            SessionId = request.SessionId ?? _sessionId,
            ProjectId = request.ProjectId ?? _projectId,
        };

        var decision = await _engine.DecideAsync(decisionRequest, cancellationToken).ConfigureAwait(false);
        return ToMindStep(decision);
    }

    public static CaseMindStep ToMindStep(CaseDecision decision)
    {
        var move = decision.Kind switch
        {
            CaseDecisionKinds.Wait => new CaseMove
            {
                Type = CaseMove.Wait,
                Text = decision.Reason ?? "wait",
                Done = false,
            },
            CaseDecisionKinds.Complete => new CaseMove
            {
                Type = CaseMove.Stop,
                Text = decision.FeedText,
                Done = true,
            },
            CaseDecisionKinds.NoAction => new CaseMove
            {
                Type = CaseMove.Wait,
                Text = decision.Reason ?? "no_action",
                Done = false,
            },
            CaseDecisionKinds.PublishFeed => new CaseMove
            {
                Type = CaseMove.Say,
                Text = decision.FeedText,
                Done = decision.Done,
                Args = AttentionArgs(decision),
            },
            CaseDecisionKinds.RaiseCase => BuildRaiseMove(decision),
            // Code selected a generation/capability task — raise a child that the runtime
            // dispatches to the registered handler (never unconstrained model next-action).
            CaseDecisionKinds.RequestGeneration => BuildRaiseMove(decision),
            CaseDecisionKinds.RequestOperation => new CaseMove
            {
                Type = CaseMove.Propose,
                Text = decision.FeedText,
                Name = decision.CapabilityId ?? Actions.ModifyNote,
                Done = false,
                Args = MergeArgs(decision, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["capability"] = JsonSerializer.SerializeToElement(decision.CapabilityId ?? Actions.ModifyNote),
                }),
            },
            _ => new CaseMove
            {
                Type = CaseMove.Wait,
                Text = "unknown_decision",
                Done = false,
            },
        };

        return new CaseMindStep(
            new CaseMindRead(
                decision.Reason ?? decision.Kind,
                Significance: 0.5,
                Urgency: decision.PresentationLevel == "alert" ? 0.8 : 0.2,
                Sensitivity: 0.2,
                new CaseMindConfidence(0.5, 0.5, 0.5),
                Needs: []),
            move,
            string.IsNullOrWhiteSpace(decision.FeedText) ? decision.Kind : decision.FeedText);
    }

    private static CaseMove BuildRaiseMove(CaseDecision decision)
    {
        var children = decision.ChildDecisions.Count > 0
            ? decision.ChildDecisions
            : [decision];

        var primary = children[0];
        var args = MergeArgs(primary, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["kind"] = JsonSerializer.SerializeToElement(MapKind(primary.CapabilityId)),
            ["objective"] = JsonSerializer.SerializeToElement(
                primary.Arguments.TryGetValue("acronym", out var acr) && acr.ValueKind == JsonValueKind.String
                    ? (acr.GetString() ?? primary.FeedText)
                    : primary.FeedText),
            ["capabilityId"] = JsonSerializer.SerializeToElement(primary.CapabilityId ?? "follow_up"),
        });
        if (primary.SourceRefs.Count > 0 && !args.ContainsKey("sourceRefs"))
            args["sourceRefs"] = JsonSerializer.SerializeToElement(primary.SourceRefs);

        if (children.Count > 1)
        {
            args["siblingRaises"] = JsonSerializer.SerializeToElement(
                children.Skip(1).Select(c =>
                {
                    var feed = c.Arguments.TryGetValue("acronym", out var a) && a.ValueKind == JsonValueKind.String
                        ? (a.GetString() ?? c.FeedText)
                        : c.FeedText;
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["capabilityId"] = c.CapabilityId,
                        ["feedText"] = feed,
                        ["kind"] = MapKind(c.CapabilityId),
                        ["presentationLevel"] = c.PresentationLevel,
                        ["acronym"] = c.Arguments.TryGetValue("acronym", out var ac) && ac.ValueKind == JsonValueKind.String
                            ? ac.GetString()
                            : null,
                        ["span"] = c.Arguments.TryGetValue("span", out var sp) && sp.ValueKind == JsonValueKind.String
                            ? sp.GetString()
                            : null,
                        ["sourceObjectId"] = c.Arguments.TryGetValue("sourceObjectId", out var so) && so.ValueKind == JsonValueKind.String
                            ? so.GetString()
                            : c.SourceRefs.FirstOrDefault(),
                        ["segmentId"] = c.Arguments.TryGetValue("segmentId", out var sg) && sg.ValueKind == JsonValueKind.String
                            ? sg.GetString()
                            : null,
                        ["sourceEventId"] = c.Arguments.TryGetValue("sourceEventId", out var se) && se.ValueKind == JsonValueKind.String
                            ? se.GetString()
                            : null,
                        ["origin"] = c.Arguments.TryGetValue("origin", out var og) && og.ValueKind == JsonValueKind.String
                            ? og.GetString()
                            : null,
                    };
                }).ToList());
        }

        return new CaseMove
        {
            Type = CaseMove.RaiseTask,
            Text = decision.FeedText,
            Name = primary.CapabilityId ?? "follow_up",
            Done = false,
            Args = args,
        };
    }

    private static Dictionary<string, JsonElement> AttentionArgs(CaseDecision decision)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(decision.PresentationLevel))
            args["attention"] = JsonSerializer.SerializeToElement(decision.PresentationLevel);
        return args;
    }

    private static Dictionary<string, JsonElement> MergeArgs(
        CaseDecision decision,
        Dictionary<string, JsonElement> extra)
    {
        var map = new Dictionary<string, JsonElement>(decision.Arguments, StringComparer.Ordinal);
        foreach (var (k, v) in extra)
            map[k] = v;
        if (!string.IsNullOrWhiteSpace(decision.PresentationLevel))
            map["attention"] = JsonSerializer.SerializeToElement(decision.PresentationLevel);
        return map;
    }

    private static string MapKind(string? capabilityId) => capabilityId switch
    {
        "conversation.note.capture" => CaseKind.Remember,
        "conversation.task.capture" => CaseKind.Organize,
        "glossary.acronym.resolve" => CaseKind.Check,
        "direct.answer" => CaseKind.Answer,
        _ => CaseKind.Check,
    };
}
