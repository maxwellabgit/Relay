using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Judgments;
using Relay.Core.Policy;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>Phase 6 exit: decision engine drives CaseRuntime via CaseMindDecisionAdapter.</summary>
public sealed class DecisionEngineRuntimeTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task Observed_and_direct_share_same_runtime_and_queue()
    {
        _tmp.Root.EnsureLayout(_clock);
        var fake = new FakeJudgmentClient()
            .Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(ScreenAnswers(0.9, 0.1, 0.1, 0.1, "persistent", 0.8)))
            .Script(QuestionSets.DirectRouteId, JudgmentResponse.FromSuccess(DirectRoute("answer", 0.85)));
        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        using var diagnostics = Diag("de-share");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        var listening = runtime.StartListening();
        runtime.IngestSegment("We decided the Atlas BESS ships in October.", _clock.UtcNow);
        var observed = await runtime.StepCaseAsync(listening.Id);
        Assert.True(observed.ChildCaseIds.Count >= 1);

        var direct = runtime.StartDirectCase("What is the Atlas beta date?");
        CaseRecord? lastDirect = null;
        for (var i = 0; i < 8; i++)
        {
            var stepped = await runtime.StepNextAsync();
            if (stepped is null) break;
            if (stepped.Origin == CaseOrigin.Direct && stepped.Id == direct.Id)
                lastDirect = stepped;
        }

        Assert.NotNull(lastDirect);
        Assert.Same(runtime.Ready, runtime.Ready);
        Assert.True(fake.CallCount >= 2);
    }

    [Fact]
    public async Task Observed_raises_multiple_bounded_child_cases()
    {
        _tmp.Root.EnsureLayout(_clock);
        var fake = new FakeJudgmentClient()
            .Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(
                ScreenAnswers(0.9, 0.9, 0.95, 0.1, "alert", 0.85)));
        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        using var diagnostics = Diag("de-multi");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        var listening = runtime.StartListening();
        // Include an acronym-like token so conversation.screen keeps the unresolved-term Noul
        // (fake ValidateAgainst requires answer keys to match the filtered question set).
        runtime.IngestSegment("We decided Max will ship Atlas BESS, and we correct the date to October 21.", _clock.UtcNow);
        var after = await runtime.StepCaseAsync(listening.Id);

        Assert.InRange(after.ChildCaseIds.Count, 2, DecisionThresholds.V1.MaxRaisesPerPass);
        Assert.All(after.ChildCaseIds, id => Assert.NotNull(runtime.GetCase(id)));
    }

    [Fact]
    public async Task Unknown_capability_cannot_execute()
    {
        _tmp.Root.EnsureLayout(_clock);
        var policy = new DecisionPolicy(enabledCapabilities: ["direct.answer@1"]);
        var fake = new FakeJudgmentClient()
            .Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(
                ScreenAnswers(0.95, 0.1, 0.1, 0.1, "persistent", 0.8)));
        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(policy: policy, client: fake));
        using var diagnostics = Diag("de-unknown");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        var listening = runtime.StartListening();
        runtime.IngestSegment("We decided the Atlas beta ships in October.", _clock.UtcNow);
        var after = await runtime.StepCaseAsync(listening.Id);

        Assert.Empty(after.ChildCaseIds);
        Assert.Empty(after.PendingOperationIds);
        Assert.Equal(0, runtime.SideEffectCount);
    }

    [Fact]
    public async Task Jev_output_cannot_create_permission_or_operation_directly()
    {
        _tmp.Root.EnsureLayout(_clock);
        var fake = new FakeJudgmentClient()
            .Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(
                ScreenAnswers(0.9, 0.1, 0.1, 0.1, "persistent", 0.8)));
        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        using var diagnostics = Diag("de-no-grant");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        var listening = runtime.StartListening();
        runtime.IngestSegment("We decided the Atlas BESS ships in October.", _clock.UtcNow);
        await runtime.StepCaseAsync(listening.Id);

        Assert.Null(runtime.GetPendingApproval(listening.Id));
        var grantsDir = Path.Combine(_tmp.Root.Path, "grants");
        Assert.False(Directory.Exists(grantsDir));
        // Raises are cases, not operations — judgment alone must not create envelopes.
        var opFiles = Directory.Exists(_tmp.Root.OperationsDirectory)
            ? Directory.GetFiles(_tmp.Root.OperationsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            : [];
        Assert.Empty(opFiles);
    }

    [Fact]
    public async Task Waiting_observed_does_not_block_direct()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, note) = local.SeedAtlasBetaDecision();

        var engine = new ScriptedDecisionEngine();
        engine.WhenObserved = req =>
        {
            if (req.PendingOperations.Any(o =>
                    o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing))
            {
                return new CaseDecision
                {
                    Kind = CaseDecisionKinds.Wait,
                    FeedText = "Waiting on pending operation.",
                    Reason = "pending_operation",
                };
            }

            return new CaseDecision
            {
                Kind = CaseDecisionKinds.RequestOperation,
                CapabilityId = Actions.ModifyNote,
                FeedText = "Correct Atlas beta date.",
                PresentationLevel = "alert",
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["capability"] = JsonSerializer.SerializeToElement(Actions.ModifyNote),
                    ["idempotencyKey"] = JsonSerializer.SerializeToElement("de-correct-" + req.CaseId),
                    ["projectId"] = JsonSerializer.SerializeToElement(project.Id),
                    ["noteId"] = JsonSerializer.SerializeToElement(note.Id),
                    ["body"] = JsonSerializer.SerializeToElement("Atlas beta ships on November 1."),
                },
            };
        };
        engine.WhenDirect = _ => new CaseDecision
        {
            Kind = CaseDecisionKinds.Complete,
            FeedText = "Ping acknowledged.",
            Done = true,
        };

        var mind = new CaseMindDecisionAdapter(engine);
        using var diagnostics = Diag("de-concurrent");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics, local: local);

        runtime.StartListening();
        runtime.IngestSegment("Correction: the Atlas beta ships on November 1.", _clock.UtcNow);
        var listenId = runtime.GetListeningCase()!.Id;
        await runtime.RunUntilIdleAsync(listenId);
        Assert.NotNull(runtime.GetPendingApproval(listenId));
        Assert.Equal(CaseStatus.Waiting, runtime.GetCase(listenId)!.Status);

        runtime.StartDirectCase("Ping from composer");
        CaseRecord? lastDirect = null;
        for (var i = 0; i < 8; i++)
        {
            var stepped = await runtime.StepNextAsync();
            if (stepped is null) break;
            if (stepped.Origin == CaseOrigin.Direct)
                lastDirect = stepped;
        }

        Assert.NotNull(lastDirect);
        Assert.Equal(CaseStatus.Completed, lastDirect!.Status);
        Assert.Equal(CaseStatus.Waiting, runtime.GetCase(listenId)!.Status);
        Assert.Equal(OperationStatus.AwaitingApproval, runtime.GetOperation(runtime.GetPendingApproval(listenId)!.OperationId)!.Status);
    }

    private RuntimeDiagnostics Diag(string runId)
        => new(Path.Combine(_tmp.Root.DevRunsDirectory, runId, "runtime.jsonl"), runId);

    public void Dispose() => _tmp.Dispose();

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

    private static JudgmentSuccess DirectRoute(string choice, double confidence) => new()
    {
        Model = "jev-1.13.0",
        Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
        {
            ["route"] = new ChoiceAnswer
            {
                Choice = choice,
                Confidence = confidence,
                Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["answer"] = choice == "answer" ? confidence : 0.05,
                    ["remember"] = choice == "remember" ? confidence : 0.05,
                    ["organize"] = choice == "organize" ? confidence : 0.05,
                    ["clarify"] = choice == "clarify" ? confidence : 0.05,
                    ["no_match"] = 0.05,
                },
            },
        },
    };

    /// <summary>Test-only engine for propose-then-wait concurrent scenarios.</summary>
    private sealed class ScriptedDecisionEngine : ICaseDecisionEngine
    {
        public Func<CaseDecisionRequest, CaseDecision>? WhenObserved { get; set; }
        public Func<CaseDecisionRequest, CaseDecision>? WhenDirect { get; set; }

        public Task<CaseDecision> DecideAsync(CaseDecisionRequest request, CancellationToken cancellationToken)
        {
            var decision = request.Origin == CaseOrigin.Observed
                ? (WhenObserved?.Invoke(request) ?? new CaseDecision { Kind = CaseDecisionKinds.Wait, FeedText = "wait" })
                : (WhenDirect?.Invoke(request) ?? new CaseDecision { Kind = CaseDecisionKinds.Complete, FeedText = "done", Done = true });
            return Task.FromResult(decision);
        }
    }
}
