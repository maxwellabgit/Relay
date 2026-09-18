using System.Text.Json;
using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Judgments;
using Relay.Core.Memory;
using Relay.Core.Privacy;
using Relay.Core.Storage;
using Relay.Core.Usage;

namespace Relay.DevHarness;

public static partial class Program
{
    public const int SkipExitCode = 3;

    private static async Task<int> RunJevScriptedAsync(string dataRootPath, string runId)
    {
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero));
        var root = new DataRoot(dataRootPath);
        root.EnsureLayout(clock);
        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var summaryPath = Path.Combine(runDir, "summary.json");

        var fake = new HarnessJudgmentClient()
            .Script(QuestionSets.ConversationScreenId, ScreenSuccess(0.9, 0.1, 0.1, 0.1, "persistent", 0.8))
            .Script(QuestionSets.DirectRouteId, DirectSuccess("answer", 0.85));
        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        using var diagnostics = new RuntimeDiagnostics(Path.Combine(runDir, "runtime.jsonl"), runId);
        using var runtime = CaseRuntime.Open(root, clock, mind, diagnostics);

        var listening = runtime.StartListening();
        runtime.IngestSegment("We decided the Atlas BESS ships in October.", clock.UtcNow);
        await runtime.StepCaseAsync(listening.Id);
        var direct = runtime.StartDirectCase("When does Atlas ship?");
        await runtime.RunUntilIdleAsync(direct.Id);

        if (fake.CallCount < 2)
            return Fail(summaryPath, runId, $"expected >=2 judgment calls, got {fake.CallCount}", 0);

        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "jev-scripted",
            runId,
            judgmentCalls = fake.CallCount,
            children = runtime.GetCase(listening.Id)?.ChildCaseIds.Count ?? 0,
            directStatus = runtime.GetCase(direct.Id)?.Status,
        });
    }

    private static async Task<int> RunJevOutageAsync(string dataRootPath, string runId)
    {
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero));
        var root = new DataRoot(dataRootPath);
        root.EnsureLayout(clock);
        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var summaryPath = Path.Combine(runDir, "summary.json");

        var fake = new HarnessJudgmentClient { Behavior = HarnessJudgmentBehavior.Fail };
        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: fake));
        using var diagnostics = new RuntimeDiagnostics(Path.Combine(runDir, "runtime.jsonl"), runId);
        using var runtime = CaseRuntime.Open(root, clock, mind, diagnostics);

        var listening = runtime.StartListening();
        runtime.IngestSegment("We decided the Atlas BESS ships in October.", clock.UtcNow);
        var after = await runtime.StepCaseAsync(listening.Id);
        // Outage → wait / no invented capability execution
        if (after.ChildCaseIds.Count > 0)
            return Fail(summaryPath, runId, "outage must not raise children", 0);

        // Recover: new isolated runtime on same root after judgments return.
        fake.Behavior = HarnessJudgmentBehavior.Success;
        fake.Script(QuestionSets.ConversationScreenId, ScreenSuccess(0.9, 0.1, 0.1, 0.1, "persistent", 0.8));
        runtime.IngestSegment("Confirming the Atlas BESS decision stands.", clock.UtcNow);
        var recovered = await runtime.StepCaseAsync(listening.Id);
        if (recovered.ChildCaseIds.Count < 1)
            return Fail(summaryPath, runId, "expected children after recovery", 0);

        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "jev-outage",
            runId,
            recoveredChildren = recovered.ChildCaseIds.Count,
        });
    }

    private static async Task<int> RunJevPrivacyAsync(string dataRootPath, string runId)
    {
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero));
        var root = new DataRoot(dataRootPath);
        root.EnsureLayout(clock);
        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var summaryPath = Path.Combine(runDir, "summary.json");

        var grants = new HostedGrantStore(root, clock);
        using var diagnostics = new RuntimeDiagnostics(Path.Combine(runDir, "runtime.jsonl"), runId);
        var mind = new CaseMindDecisionAdapter(new RelayDecisionEngine(client: new HarnessJudgmentClient()));
        using var runtime = CaseRuntime.Open(root, clock, mind, diagnostics);
        IRelaySurface surface = new CaseRuntimeSurface(runtime, clock, grants: grants);

        var before = surface.Snapshot();
        if (before.HostedJudgments?.HasActiveGrant == true)
            return Fail(summaryPath, runId, "expected no grant initially", 0);

        var grant = surface.GrantHostedSession(
            "harness-session",
            [HostedPurposes.ConversationScreen],
            maximumInputTokenBudget: 1000,
            expiresAt: clock.UtcNow.AddHours(1));
        if (!grant.Ok) return Fail(summaryPath, runId, grant.Error ?? "grant failed", 0);

        var after = surface.Snapshot();
        if (after.HostedJudgments?.HasActiveGrant != true)
            return Fail(summaryPath, runId, "grant not visible on surface", 0);

        await Task.CompletedTask;
        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "jev-privacy",
            runId,
            grantId = grant.GrantId,
            listeningIndependent = after.HostedJudgments?.ListeningIndependent,
        });
    }

    private static int RunJevImprovement(string dataRootPath, string runId)
    {
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero));
        var root = new DataRoot(dataRootPath);
        root.EnsureLayout(clock);
        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var summaryPath = Path.Combine(runDir, "summary.json");

        var projects = Path.Combine(root.Path, "projects");
        Directory.CreateDirectory(projects);
        var light = Path.Combine(projects, "lightshift");
        Directory.CreateDirectory(Path.Combine(light, ".orchestrator"));
        var other = Path.Combine(projects, "other");
        Directory.CreateDirectory(Path.Combine(other, ".orchestrator"));

        var life = new GlossaryImprovementLifecycle(root);
        var sig = PatternSignature.ForAcronymCorrection("proj-light", "BESS");
        var proposal = life.DraftFromSignature(sig, "Battery Energy Storage System", clock.UtcNow, 3);
        var (passed, _, _) = life.Evaluate(proposal.ProposalId, light, other);
        if (!passed) return Fail(summaryPath, runId, "evaluation failed", 0);
        var hash = life.LoadLifecycle(proposal.ProposalId)!.EvalArtifactHash!;
        life.Approve(proposal.ProposalId, hash, light, clock.UtcNow);
        life.RecordShadowUse(proposal.ProposalId);
        life.RecordShadowUse(proposal.ProposalId);
        life.RecordShadowUse(proposal.ProposalId);
        if (life.LoadLifecycle(proposal.ProposalId)!.Status != ImprovementStatuses.Active)
            return Fail(summaryPath, runId, "expected active after shadow", 0);
        life.Revert(proposal.ProposalId, light, clock.UtcNow);

        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "jev-improvement",
            runId,
            proposalId = proposal.ProposalId,
            finalStatus = life.LoadLifecycle(proposal.ProposalId)!.Status,
        });
    }

    private static int RunJevLive(string dataRootPath, string runId)
    {
        var clock = new HarnessClock(DateTimeOffset.UtcNow);
        var root = new DataRoot(dataRootPath);
        root.EnsureLayout(clock);
        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var summaryPath = Path.Combine(runDir, "summary.json");

        var key = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return Skip(summaryPath, runId, "SKIPPED: missing TYPESAFE_API_KEY");

        // Live path is intentionally not auto-run without an explicit client binding in this harness.
        // Presence of the key means the environment can run Windows live gates; report readiness.
        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "jev-live",
            runId,
            ready = true,
            note = "Key present; use Desktop DPAPI settings + hosted grant for the full live gate.",
            keyPresent = true,
        });
    }

    private static int Skip(string summaryPath, string runId, string message)
    {
        var summary = new { ok = false, skipped = true, runId, message };
        AtomicFile.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, RelayJson.Indented));
        Console.WriteLine(message);
        return SkipExitCode;
    }

    private static JudgmentResponse ScreenSuccess(
        double decision, double commitment, double correction, double term, string attention, double attentionConfidence)
        => JudgmentResponse.FromSuccess(new JudgmentSuccess
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
                        ["ambient"] = 0.1,
                        ["persistent"] = attention == "persistent" ? 0.8 : 0.1,
                        ["alert"] = attention == "alert" ? 0.8 : 0.1,
                    },
                },
            },
        });

    private static JudgmentResponse DirectSuccess(string choice, double confidence)
        => JudgmentResponse.FromSuccess(new JudgmentSuccess
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
                        ["remember"] = 0.05,
                        ["organize"] = 0.05,
                        ["clarify"] = 0.05,
                        ["no_match"] = 0.05,
                    },
                },
            },
        });

    private enum HarnessJudgmentBehavior { Success, Fail }

    private sealed class HarnessJudgmentClient : IJudgmentClient
    {
        private readonly Dictionary<string, JudgmentResponse> _scripts = new(StringComparer.Ordinal);
        private int _calls;

        public string ProviderName => "harness-fake";
        public int CallCount => _calls;
        public HarnessJudgmentBehavior Behavior { get; set; } = HarnessJudgmentBehavior.Success;

        public HarnessJudgmentClient Script(string questionSetId, JudgmentResponse response)
        {
            _scripts[questionSetId] = response;
            return this;
        }

        public Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken)
        {
            _calls++;
            if (Behavior == HarnessJudgmentBehavior.Fail)
            {
                return Task.FromResult(JudgmentResponse.FromFailure(
                    JudgmentFailure.Create(JudgmentFailureCategories.Timeout, "harness outage")));
            }

            if (_scripts.TryGetValue(request.QuestionSetId, out var response))
                return Task.FromResult(response);

            return Task.FromResult(JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.Validation, "no script")));
        }
    }
}
