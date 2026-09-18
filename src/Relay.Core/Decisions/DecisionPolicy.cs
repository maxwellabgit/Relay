using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Judgments;
using Relay.Core.Privacy;

namespace Relay.Core.Decisions;

/// <summary>Combines typed judgment answers with thresholds into CaseDecision directives.</summary>
public sealed class DecisionPolicy
{
    private readonly DecisionThresholds _thresholds;
    private readonly HashSet<string> _enabledCapabilities;

    public DecisionPolicy(DecisionThresholds? thresholds = null, IEnumerable<string>? enabledCapabilities = null)
    {
        _thresholds = thresholds ?? DecisionThresholds.V1;
        _enabledCapabilities = new HashSet<string>(
            enabledCapabilities ??
            [
                "conversation.note.capture@1",
                "conversation.task.capture@1",
                "glossary.acronym.resolve@1",
                "direct.answer@1",
            ],
            StringComparer.Ordinal);
    }

    public DecisionThresholds Thresholds => _thresholds;

    public CaseDecision ApplyConversationScreen(JudgmentSuccess answers, CaseDecisionRequest request)
    {
        var raises = new List<CaseDecision>();
        var noul = (string id) =>
            answers.Answers.TryGetValue(id, out var a) && a is NoulAnswer n ? n.ProbabilityYes : 0;

        if (noul("contains_durable_decision") >= _thresholds.DecisionCandidate)
        {
            raises.Add(FeedOrCapability(
                "conversation.note.capture@1",
                "Possible durable decision noted.",
                "persistent",
                request));
        }

        if (noul("contains_actionable_commitment") >= _thresholds.CommitmentCandidate)
        {
            raises.Add(FeedOrCapability(
                "conversation.task.capture@1",
                "Possible commitment noted.",
                "persistent",
                request));
        }

        if (noul("contains_correction") >= _thresholds.CorrectionCandidate)
        {
            raises.Add(FeedOrCapability(
                "conversation.note.capture@1",
                "Possible correction noted.",
                "alert",
                request));
        }

        if (noul("contains_unresolved_term_request") >= _thresholds.UnresolvedTermCandidate)
        {
            raises.Add(FeedOrCapability(
                "glossary.acronym.resolve@1",
                "Unresolved term noted.",
                "persistent",
                request));
        }

        var attention = "ambient";
        if (answers.Answers.TryGetValue("attention", out var att) && att is ChoiceAnswer choice)
        {
            if (choice.Choice == "alert" && choice.Confidence >= _thresholds.AlertConfidence)
                attention = "alert";
            else if (choice.Choice is "persistent" or "alert")
                attention = "persistent";
        }

        raises = raises.Take(_thresholds.MaxRaisesPerPass).ToList();
        if (raises.Count == 0)
        {
            return new CaseDecision
            {
                Kind = CaseDecisionKinds.Wait,
                PresentationLevel = attention,
                FeedText = "Listening.",
                Reason = "no_positive_labels",
            };
        }

        if (raises.Count == 1)
        {
            var only = raises[0];
            return new CaseDecision
            {
                Kind = only.Kind,
                CapabilityId = only.CapabilityId,
                CapabilityVersion = only.CapabilityVersion,
                Arguments = only.Arguments,
                JudgmentIds = only.JudgmentIds,
                SourceRefs = only.SourceRefs,
                PresentationLevel = only.PresentationLevel ?? attention,
                FeedText = only.FeedText,
                Done = only.Done,
                ChildDecisions = only.ChildDecisions,
                Reason = only.Reason,
            };
        }

        return new CaseDecision
        {
            Kind = CaseDecisionKinds.RaiseCase,
            PresentationLevel = attention,
            FeedText = $"Raising {raises.Count} follow-ups.",
            ChildDecisions = raises,
            Reason = "multi_label",
        };
    }

    public CaseDecision ApplyDirectRoute(JudgmentSuccess answers, CaseDecisionRequest request)
    {
        if (!answers.Answers.TryGetValue("route", out var raw) || raw is not ChoiceAnswer route)
        {
            return Clarify("Could not route the request.");
        }

        if (route.Choice is "clarify" or ChoiceQuestion.NoMatch ||
            route.Confidence < _thresholds.DirectRouteConfidence ||
            !route.Probabilities.TryGetValue(route.Choice, out var win) ||
            win < _thresholds.DirectRouteWinner)
        {
            return Clarify("Need a clearer request.");
        }

        var capability = route.Choice switch
        {
            "answer" => "direct.answer@1",
            "remember" => "conversation.note.capture@1",
            "organize" => "conversation.task.capture@1",
            _ => null,
        };

        if (capability is null || !_enabledCapabilities.Contains(capability))
            return Clarify("That route is not available.");

        return new CaseDecision
        {
            Kind = CaseDecisionKinds.RequestGeneration,
            CapabilityId = capability.Split('@')[0],
            CapabilityVersion = 1,
            PresentationLevel = "finding",
            FeedText = $"Routing to {capability}.",
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["route"] = JsonSerializer.SerializeToElement(route.Choice),
                ["objective"] = JsonSerializer.SerializeToElement(request.Objective ?? ""),
            },
            Reason = "direct_route",
        };
    }

    public CaseDecision UnknownCapabilityRejected(string capabilityId) => new()
    {
        Kind = CaseDecisionKinds.NoAction,
        FeedText = "Capability is not enabled.",
        Reason = $"unknown_capability:{capabilityId}",
    };

    private CaseDecision FeedOrCapability(
        string capabilityAtVersion,
        string feed,
        string level,
        CaseDecisionRequest request)
    {
        if (!_enabledCapabilities.Contains(capabilityAtVersion))
            return UnknownCapabilityRejected(capabilityAtVersion);

        var parts = capabilityAtVersion.Split('@');
        return new CaseDecision
        {
            Kind = CaseDecisionKinds.RaiseCase,
            CapabilityId = parts[0],
            CapabilityVersion = int.Parse(parts[1]),
            PresentationLevel = level,
            FeedText = feed,
            SourceRefs = request.RecentSegments.Select(s => s.ObjectId).ToList(),
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["origin"] = JsonSerializer.SerializeToElement(request.Origin),
            },
        };
    }

    private static CaseDecision Clarify(string text) => new()
    {
        Kind = CaseDecisionKinds.PublishFeed,
        PresentationLevel = "persistent",
        FeedText = text,
        Reason = "clarify",
    };
}
