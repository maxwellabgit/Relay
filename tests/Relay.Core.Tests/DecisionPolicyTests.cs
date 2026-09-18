using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Judgments;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

public sealed class DecisionPolicyTests
{
    private readonly DecisionPolicy _policy = new();

    [Fact]
    public void Threshold_boundary_creates_note_candidate()
    {
        var success = ScreenAnswers(
            decision: 0.80,
            commitment: 0.10,
            correction: 0.10,
            term: 0.10,
            attention: "persistent",
            attentionConfidence: 0.8);

        var decision = _policy.ApplyConversationScreen(success, ObservedRequest());
        Assert.Equal(CaseDecisionKinds.RaiseCase, decision.Kind);
        Assert.Equal("conversation.note.capture", decision.CapabilityId);
    }

    [Fact]
    public void Below_negative_ceiling_is_wait()
    {
        var success = ScreenAnswers(0.2, 0.2, 0.2, 0.2, "ambient", 0.9);
        var decision = _policy.ApplyConversationScreen(success, ObservedRequest());
        Assert.Equal(CaseDecisionKinds.Wait, decision.Kind);
    }

    [Fact]
    public void Multi_label_raises_bounded_children()
    {
        var success = ScreenAnswers(0.9, 0.9, 0.95, 0.1, "alert", 0.85);
        var decision = _policy.ApplyConversationScreen(success, ObservedRequest());
        Assert.Equal(CaseDecisionKinds.RaiseCase, decision.Kind);
        Assert.True(decision.ChildDecisions.Count is >= 2 and <= 2);
    }

    [Fact]
    public void Unknown_capability_cannot_execute()
    {
        var policy = new DecisionPolicy(enabledCapabilities: ["direct.answer@1"]);
        var success = ScreenAnswers(0.95, 0.1, 0.1, 0.1, "persistent", 0.8);
        var decision = policy.ApplyConversationScreen(success, ObservedRequest());
        Assert.Equal(CaseDecisionKinds.NoAction, decision.Kind);
        Assert.Contains("unknown_capability", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_route_clarify_on_low_confidence()
    {
        var success = new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["route"] = new ChoiceAnswer
                {
                    Choice = "answer",
                    Confidence = 0.4,
                    Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["answer"] = 0.7,
                        ["remember"] = 0.1,
                        ["organize"] = 0.1,
                        ["clarify"] = 0.05,
                        ["no_match"] = 0.05,
                    },
                },
            },
        };

        var decision = _policy.ApplyDirectRoute(success, DirectRequest());
        Assert.Equal(CaseDecisionKinds.PublishFeed, decision.Kind);
        Assert.Equal("clarify", decision.Reason);
    }

    [Fact]
    public async Task Engine_with_fake_client_routes_observed_and_direct()
    {
        var observedScript = JudgmentResponse.FromSuccess(ScreenAnswers(0.9, 0.1, 0.1, 0.1, "persistent", 0.8));
        var directScript = JudgmentResponse.FromSuccess(new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["route"] = new ChoiceAnswer
                {
                    Choice = "answer",
                    Confidence = 0.8,
                    Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["answer"] = 0.8,
                        ["remember"] = 0.05,
                        ["organize"] = 0.05,
                        ["clarify"] = 0.05,
                        ["no_match"] = 0.05,
                    },
                },
            },
        });

        var fake = new FakeJudgmentClient()
            .Script(QuestionSets.ConversationScreenId, observedScript)
            .Script(QuestionSets.DirectRouteId, directScript);
        var engine = new RelayDecisionEngine(client: fake);

        var observed = await engine.DecideAsync(ObservedRequest(withSegment: true), CancellationToken.None);
        Assert.Equal("conversation.note.capture", observed.CapabilityId);

        var direct = await engine.DecideAsync(DirectRequest(), CancellationToken.None);
        Assert.Equal(CaseDecisionKinds.RequestGeneration, direct.Kind);
        Assert.Equal("direct.answer", direct.CapabilityId);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task Adapter_maps_decision_to_mind_step()
    {
        var fake = new FakeJudgmentClient().Script(QuestionSets.DirectRouteId, JudgmentResponse.FromSuccess(new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["route"] = new ChoiceAnswer
                {
                    Choice = "clarify",
                    Confidence = 0.9,
                    Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["answer"] = 0.05,
                        ["remember"] = 0.05,
                        ["organize"] = 0.05,
                        ["clarify"] = 0.8,
                        ["no_match"] = 0.05,
                    },
                },
            },
        }));
        ICaseMind mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        var step = await mind.StepAsync(new CaseMindRequest(
            "c1", CaseOrigin.Direct, CaseKind.Answer, "what?", 1, CaseStatus.Active,
            [], [], [], DateTimeOffset.UtcNow, 0, []), CancellationToken.None);
        Assert.Equal(CaseMove.Say, step.Move.Type);
        Assert.Contains("clearer", step.Feed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jev_output_cannot_create_permission_or_operation_directly()
    {
        // Policy only emits CaseDecision kinds; operations still require the broker.
        Assert.DoesNotContain(CaseDecisionKinds.All, k => k is "grant_permission" or "execute");
        var rejected = _policy.UnknownCapabilityRejected("evil.tool@1");
        Assert.Equal(CaseDecisionKinds.NoAction, rejected.Kind);
    }

    private static JudgmentSuccess ScreenAnswers(
        double decision,
        double commitment,
        double correction,
        double term,
        string attention,
        double attentionConfidence) => new()
    {
        Model = "jev-1.13.0",
        Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
        {
            ["contains_durable_decision"] = new NoulAnswer { ProbabilityYes = decision },
            ["contains_actionable_commitment"] = new NoulAnswer { ProbabilityYes = commitment },
            ["contains_correction"] = new NoulAnswer { ProbabilityYes = correction },
            ["contains_unresolved_term_request"] = new NoulAnswer { ProbabilityYes = term },
            ["attention"] = new ChoiceAnswer
            {
                Choice = attention,
                Confidence = attentionConfidence,
                Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["ambient"] = attention == "ambient" ? 0.8 : 0.1,
                    ["persistent"] = attention == "persistent" ? 0.8 : 0.1,
                    ["alert"] = attention == "alert" ? 0.8 : 0.1,
                },
            },
        },
    };

    private static CaseDecisionRequest ObservedRequest(bool withSegment = false) => new()
    {
        CaseId = "obs-1",
        Origin = CaseOrigin.Observed,
        Kind = CaseKind.Check,
        Version = 1,
        At = DateTimeOffset.UtcNow,
        RecentSegments = withSegment
            ?
            [
                new ListeningSegmentView("seg1", "evt1", "obj1", "hash1", DateTimeOffset.UtcNow, null, "We decided the Atlas BESS ships in October."),
            ]
            : [],
    };

    private static CaseDecisionRequest DirectRequest() => new()
    {
        CaseId = "dir-1",
        Origin = CaseOrigin.Direct,
        Kind = CaseKind.Answer,
        Objective = "What is the Atlas beta date?",
        Version = 1,
        At = DateTimeOffset.UtcNow,
    };
}
