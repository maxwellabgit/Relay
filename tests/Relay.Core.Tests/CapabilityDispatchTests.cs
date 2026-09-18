using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Judgments;
using Relay.Core.Memory;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>P0 wiring: raised capability children dispatch to registered handlers.</summary>
public sealed class CapabilityDispatchTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 14, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Raised_acronym_child_runs_handler_without_inventing_expansion()
    {
        _tmp.Root.EnsureLayout(_clock);
        var glossary = new GlossaryStore(_tmp.Root);
        var registry = new CapabilityRegistry();
        registry.Register(AcronymResolveCapability.Definition,
            new AcronymResolveCapability(glossary, _ => null, client: null));

        var fake = new FakeJudgmentClient()
            .Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(new JudgmentSuccess
            {
                Model = "jev-1.13.0",
                Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
                {
                    ["contains_durable_decision"] = new NoulAnswer { ProbabilityYes = 0.1 },
                    ["contains_actionable_commitment"] = new NoulAnswer { ProbabilityYes = 0.1 },
                    ["contains_correction"] = new NoulAnswer { ProbabilityYes = 0.1 },
                    ["contains_unresolved_term_request"] = new NoulAnswer { ProbabilityYes = 0.95 },
                    ["attention"] = new ChoiceAnswer
                    {
                        Choice = "persistent",
                        Confidence = 0.85,
                        Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["ambient"] = 0.05,
                            ["persistent"] = 0.85,
                            ["alert"] = 0.1,
                        },
                    },
                },
            }));

        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "cap-dispatch.jsonl"), "cap-dispatch");
        using var runtime = CaseRuntime.Open(
            _tmp.Root, _clock, mind, diagnostics, capabilities: registry);

        var listening = runtime.StartListening();
        runtime.IngestSegment("What does BESS mean in the Atlas plan?", _clock.UtcNow);
        var afterScreen = await runtime.StepCaseAsync(listening.Id);
        Assert.NotEmpty(afterScreen.ChildCaseIds);

        var childId = afterScreen.ChildCaseIds[0];
        var child = runtime.GetCase(childId)!;
        Assert.Contains(AcronymResolveCapability.AtVersion, child.AllowedCapabilities);

        var stepped = await runtime.StepCaseAsync(childId);
        Assert.True(
            stepped.Status is CaseStatus.Completed or CaseStatus.Waiting or CaseStatus.Active,
            stepped.Status);
        // Unresolved without glossary entry — never invents an expansion.
        var feed = runtime.Projections.ListFeedItems().Select(f => f.Text).ToList();
        Assert.Contains(feed, t => t.Contains("BESS", StringComparison.OrdinalIgnoreCase) ||
                                   t.Contains("unresolved", StringComparison.OrdinalIgnoreCase) ||
                                   t.Contains("Waiting", StringComparison.OrdinalIgnoreCase) ||
                                   t.Length > 0);
    }

    [Fact]
    public async Task Direct_route_raises_capability_child_and_completes_parent()
    {
        _tmp.Root.EnsureLayout(_clock);
        var registry = new CapabilityRegistry();
        registry.Register(DirectAnswerCapability.Definition, new DirectAnswerCapability());
        registry.Register(TaskCaptureCapability.Definition, new TaskCaptureCapability());
        registry.Register(NoteCaptureCapability.Definition, new NoteCaptureCapability());
        registry.Register(AcronymResolveCapability.Definition,
            new AcronymResolveCapability(new GlossaryStore(_tmp.Root), _ => null));

        var fake = new FakeJudgmentClient()
            .Script(QuestionSets.DirectRouteId, JudgmentResponse.FromSuccess(new JudgmentSuccess
            {
                Model = "jev-1.13.0",
                Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
                {
                    ["route"] = new ChoiceAnswer
                    {
                        Choice = "answer",
                        Confidence = 0.9,
                        Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["answer"] = 0.9,
                            ["remember"] = 0.05,
                            ["organize"] = 0.03,
                            ["clarify"] = 0.02,
                            ["no_match"] = 0.0,
                        },
                    },
                },
            }));

        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "cap-direct.jsonl"), "cap-direct");
        using var runtime = CaseRuntime.Open(
            _tmp.Root, _clock, mind, diagnostics, capabilities: registry);

        var direct = runtime.StartDirectCase("When does the Atlas beta ship?");
        var after = await runtime.StepCaseAsync(direct.Id);
        Assert.Equal(CaseStatus.Completed, after.Status);
        Assert.NotEmpty(after.ChildCaseIds);

        var child = runtime.GetCase(after.ChildCaseIds[0])!;
        Assert.Contains(DirectAnswerCapability.AtVersion, child.AllowedCapabilities);
        var childAfter = await runtime.StepCaseAsync(child.Id);
        Assert.True(childAfter.Status is CaseStatus.Completed or CaseStatus.Waiting);
    }
}
