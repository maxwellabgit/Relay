using Relay.Core.Config;
using Relay.Core.Evaluation;
using Relay.Core.Judge;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Preferences;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Gateway;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// Slice 8: improve tasks carry a contract (benefit, permissions, scope, acceptance) that policy
/// enforces on Relay's own proposals; approved self-changes are change sets; and an evaluation harness
/// scores a planner and a judge against recorded, unseen, and failure cases, refusing a set in which
/// recorded cases stand alone.
/// </summary>
public class EvaluationTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static readonly string CasesDirectory = Path.Combine(AppContext.BaseDirectory, "Evaluation");

    private static readonly string[] BackyardNotes =
    [
        "Idea: build a pergola over the backyard patio.",
        "Task: clear out the backyard shed before winter.",
        "We decided the backyard fence gets replaced in spring.",
    ];

    /// <summary>The world the authored cases are written against: Atlas with its beta decision, Home with the backyard notes and one about the kitchen.</summary>
    private static Scenario World(TempRoot tmp, IOrchestrator? orchestrator = null, Action<RelaySettings>? configure = null)
    {
        var s = Scenario.New(tmp, configure, orchestrator).WithWorkspace()
            .Command("create project Atlas").Approve().ExpectProject("atlas")
            .Command("create project Home").Approve().ExpectProject("home")
            .Note("We decided the Atlas beta ships on October 14.");
        foreach (var text in BackyardNotes) s.Note(text);
        s.Note("Idea: repaint the kitchen cabinets.");
        return s.Command("file all notes under Home").ExpectState(RelayState.Completed);
    }

    private static JudgeContext WorldJudgeContext(EvaluationCase _) => new(["Atlas", "Home"], ["CAD"], [], null);

    private static EvaluationSet Authored() => EvaluationSet.Load(CasesDirectory);

    // ----------------------------------------------------------------------------------------
    // The improvement contract
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("keep responses concise")]
    [InlineData("always show what CAD means")]
    [InlineData("allow online search")]
    public void EverySelfChangeTheGrammarProposesStatesItsContract(string instruction)
    {
        using var h = new Harness(_tmp.Root).Start();
        var plan = Plan(h, instruction);
        Assert.True(plan.Understood);
        Assert.NotEmpty(plan.Proposals);
        foreach (var p in plan.Proposals)
        {
            Assert.Contains(p.Action, new[] { Actions.UpdatePreference, Actions.UpdatePrompt });
            foreach (var key in PolicyEngine.ContractKeys) Assert.False(string.IsNullOrWhiteSpace(p.Target.GetValueOrDefault(key)), $"{p.Action} lacks {key}");
            var (_, detail) = ProposalText.Describe(p, null);
            Assert.Contains("Benefit: ", detail);
            Assert.Contains("Permissions: ", detail);
            Assert.Contains("Scope: ", detail);
            Assert.Contains("Acceptance: ", detail);
        }
    }

    [Fact]
    public void PolicyDeniesABareSelfChangeFromAPlannerInAnImproveTaskAndNamesEveryMissingField()
    {
        using var h = new Harness(_tmp.Root).Start();
        PolicyWorld WorldOf(TaskKind kind) => new() { Registry = h.Registry, Roots = h.Roots, DataRoot = h.Root, DraftNoteExists = _ => false, ProjectNoteExists = (_, _) => false, Origin = TaskOrigin.Direct, Kind = kind };
        var bare = new Proposal("01HZZZZZZZZZZZZZZZZZZZZZZ1", Actions.UpdatePreference, "model says so",
            new Dictionary<string, string> { ["key"] = "response.verbosity", ["value"] = "concise" }, ["01HZZZZZZZZZZZZZZZZZZZZZZ0"], [], Risks.ControlledWrite, true, Producers.Model);
        var improve = WorldOf(TaskKind.Improve);

        var denied = PolicyEngine.Decide(bare, improve);
        Assert.Equal(DecisionOutcome.Deny, denied.Outcome);
        foreach (var key in PolicyEngine.ContractKeys) Assert.Contains(denied.Reasons, r => r.Contains($"target.{key}"));

        // With the four fields the same proposal needs approval like any controlled write.
        var contracted = bare with { Target = new Dictionary<string, string>(bare.Target) { ["benefit"] = "Shorter answers", ["permissions"] = "preferences.json", ["scope"] = "one key", ["acceptance"] = "next answer under 700 chars" } };
        Assert.Equal(DecisionOutcome.NeedsApproval, PolicyEngine.Decide(contracted, improve).Outcome);

        // A contract field that is present is still bounded.
        var bloated = contracted with { Target = new Dictionary<string, string>(contracted.Target) { ["benefit"] = new string('x', 401) } };
        Assert.Contains(PolicyEngine.Decide(bloated, improve).Reasons, r => r.Contains("target.benefit is longer"));

        // Outside an improve task the contract is optional (the fields are validated only when given).
        Assert.Equal(DecisionOutcome.NeedsApproval, PolicyEngine.Decide(bare, WorldOf(TaskKind.Organize)).Outcome);

        // The user's own Settings click is configuration, not an improvement Relay argues for: no contract owed.
        var user = bare with { ProposedBy = Producers.User, SourceEventIds = [] };
        Assert.Equal(DecisionOutcome.NeedsApproval, PolicyEngine.Decide(user, improve).Outcome);
    }

    [Fact]
    public void ThroughTheCoordinatorABarePlannerSelfChangeIsDeniedOnRecordAndAContractedOneBecomesAChangeSetWithItsAcceptance()
    {
        // A planner (standing in for the model) that proposes the change without the contract.
        var bare = new CannedOrchestrator().Otherwise((request, _) => new TurnPlan(true, "Make responses concise", [], null, [],
            [new Proposal(Relay.Core.Ids.Ulid.NewUlid(request.At), Actions.UpdatePreference, "asked for concise answers",
                new Dictionary<string, string> { ["key"] = "response.verbosity", ["value"] = "concise" }, [request.SourceEventId], [], Risks.ControlledWrite, true, Producers.Model)], "canned"));
        using (var s = Scenario.New(_tmp, orchestrator: bare).WithWorkspace()
            .Command("keep responses concise")
            .ExpectTask(TaskKind.Improve, TaskStatus.Completed, TaskOrigin.Direct)
            .ExpectProposal(Actions.UpdatePreference, "denied")
            .ExpectOutcome("denied"))
        {
            var task = s.FindTask(TaskKind.Improve)!;
            Assert.Contains(task.Proposals[0].Reasons, r => r.Contains("must state its benefit"));
            Assert.Contains(task.Proposals[0].Reasons, r => r.Contains("must state its acceptance"));
            Assert.Empty(s.Snap.ChangeSets);
            Assert.Contains("Nothing ran", s.Snap.Receipt);
        }

        // The grammar's own proposal carries the contract; approving it applies a change set that records the acceptance criterion.
        using var tmp2 = new TempRoot();
        using var g = Scenario.New(tmp2).WithWorkspace()
            .Command("always show what CAD means")
            .ExpectTask(TaskKind.Improve, TaskStatus.AwaitingApproval, TaskOrigin.Direct)
            .ExpectProposal(Actions.UpdatePreference, "pending");
        var card = Assert.Single(g.Response.Proposals);
        Assert.Contains("Benefit: 'CAD' is defined on screen", card.Detail);
        Assert.Contains("Acceptance: Hearing 'CAD'", card.Detail);
        g.Approve().ExpectState(RelayState.Completed).ExpectEvent(EventTypes.ChangeSetApplied);
        var applied = g.H.Last(EventTypes.ChangeSetApplied)!;
        Assert.Contains("Hearing 'CAD'", applied.DataString("acceptance"));
        var set = Assert.Single(g.Snap.ChangeSets);
        Assert.False(set.Reverted);
        g.Do("Revert", c => Assert.True(c.RevertChangeSet(set.ChangeSetId))).ExpectPreference("display.alwaysShow", "");
    }

    [Fact]
    public void TheModelIsToldAboutTheContract()
    {
        using var h = new Harness(_tmp.Root).Start();
        var prompt = ModelOrchestrator.SystemPrompt(h.PlannerContext());
        Assert.Contains("update_preference {key, value, benefit, permissions, scope, acceptance}", prompt);
        Assert.Contains("an improve task without all four is denied", prompt);
    }

    // ----------------------------------------------------------------------------------------
    // Recorded cases
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void RecordedCasesTakeTheUsersResponseAsTheLabel()
    {
        static TaskDiagnostics Task(string planner, string outcome, params (string Action, string Status)[] proposals) => new()
        {
            TaskId = "T" + Guid.NewGuid().ToString("N")[..8],
            Origin = "direct", Kind = "organize", Status = outcome == "failed" ? "failed" : "completed", Outcome = outcome, Planner = planner,
            FocusedPrompt = "move the backyard notes into Garden", SourceEventId = "E1", Presentation = "proposal", StartedAt = Harness.T0,
            Proposals = proposals.Select(p => new ProposalRecord("P" + p.Action, p.Action, new Dictionary<string, string>(), "RequiresApproval", p.Status, [], [], null, null, null)).ToList(),
        };

        // Accepted whole: the same actions are expected again.
        var accepted = RecordedCases.From(Task("rules", "executed", (Actions.CreateProject, "executed"), (Actions.MoveNote, "executed"), (Actions.MoveNote, "executed")))!;
        Assert.Equal(CaseSources.Recorded, accepted.Source);
        Assert.Equal(["create_project", "move_note", "move_note"], accepted.Expect.Actions!);
        Assert.True(accepted.Expect.Understood);
        Assert.Null(accepted.Expect.ForbiddenActions);

        // Partly rejected: understood, but the rejection is a note, not a label.
        var rejected = RecordedCases.From(Task("rules", "rejected", (Actions.CreateProject, "rejected"), (Actions.MoveNote, "skipped")))!;
        Assert.Null(rejected.Expect.Actions);
        Assert.Null(rejected.Expect.ForbiddenActions);
        Assert.Contains("rejected by the user (not a label): create_project", rejected.Why);

        // Denied by policy: the planner must not propose it again.
        var denied = RecordedCases.From(Task("model:x", "denied", (Actions.DeleteProject, "denied")))!;
        Assert.Equal(["delete_project"], denied.Expect.ForbiddenActions!);
        Assert.Null(denied.Expect.Actions);

        // Answered with nothing proposed: no proposals are expected.
        var answered = RecordedCases.From(Task("rules", "answered"))!;
        Assert.Empty(answered.Expect.Actions!);

        // Not a planner's work, or never reached the user: no case.
        Assert.Null(RecordedCases.From(Task(Producers.User, "executed", (Actions.UpdatePreference, "executed"))));
        Assert.Null(RecordedCases.From(Task("rules", "failed", (Actions.CreateProject, "failed"))));
        Assert.Null(RecordedCases.From(Task("rules", "cancelled")));
    }

    // ----------------------------------------------------------------------------------------
    // The completeness guard
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordedCasesAloneAreRefusedAndTheGuardNamesEveryGap()
    {
        var recorded = new EvaluationCase { Id = "recorded:1", Source = CaseSources.Recorded, Kind = "answer", Instruction = "list projects", Expect = new Expectation { Actions = [] } };
        var onlyRecorded = new EvaluationSet([recorded]);
        var problems = onlyRecorded.Validate();
        Assert.Contains(problems, p => p.Contains("recorded cases alone are never the whole evaluation set"));
        Assert.Contains(problems, p => p.Contains("No failure cases"));
        Assert.Contains(problems, p => p.Contains("'answer' lane but no unseen case does"));

        // The runner refuses rather than scoring a flattering set.
        var runner = new EvaluationRunner(new RuleBasedOrchestrator(), _ => throw new InvalidOperationException("must not plan"));
        var report = await runner.RunAsync(onlyRecorded, CancellationToken.None);
        Assert.False(report.Passed);
        Assert.Empty(report.Results);
        Assert.Equal(problems, report.Problems);
        Assert.Contains("guard: No unseen cases", report.Render());

        // Malformed cases are each named.
        var malformed = new EvaluationSet(
        [
            recorded,
            recorded,                                                                                                                                  // duplicate id
            new EvaluationCase { Id = "u1", Source = CaseSources.Unseen, Kind = "answer", Instruction = "List projects.", Expect = new Expectation { Actions = [] } },   // repeats a recorded instruction
            new EvaluationCase { Id = "u2", Source = CaseSources.Unseen, Kind = "answer", Instruction = "what is new", Expect = new Expectation() },                   // checks nothing
            new EvaluationCase { Id = "f1", Source = CaseSources.Failure, Kind = "organize", Instruction = "delete project X", Expect = new Expectation { ForbiddenActions = ["delete_project"] } }, // no why
            new EvaluationCase { Id = "f2", Source = CaseSources.Failure, Origin = "observed", Segments = ["hello there"], Expect = new Expectation { Actions = [] }, Why = "x" },              // judge case with planner expectations
            new EvaluationCase { Id = "f3", Source = "guess", Origin = "elsewhere", Kind = "poetry", Instruction = "x", Expect = new Expectation { Actions = ["fly"] }, Why = "x" },              // unknown everything
        ]);
        problems = malformed.Validate();
        Assert.Contains(problems, p => p.Contains("'recorded:1' appears 2 times"));
        Assert.Contains(problems, p => p.Contains("'u1' repeats a recorded instruction"));
        Assert.Contains(problems, p => p.Contains("'u2': the expectation checks nothing"));
        Assert.Contains(problems, p => p.Contains("'f1': a failure case must say which mistake"));
        Assert.Contains(problems, p => p.Contains("'f2': a judge case (segments) cannot expect planner output"));
        Assert.Contains(problems, p => p.Contains("unknown source 'guess'"));
        Assert.Contains(problems, p => p.Contains("unknown origin 'elsewhere'"));
        Assert.Contains(problems, p => p.Contains("unknown kind 'poetry'"));
        Assert.Contains(problems, p => p.Contains("'fly' is not an action Relay knows"));

        // A set with all three sources, held out and explained, passes the guard.
        var complete = new EvaluationSet(
        [
            recorded,
            new EvaluationCase { Id = "u1", Source = CaseSources.Unseen, Kind = "answer", Instruction = "show me all projects", Expect = new Expectation { Actions = [] } },
            new EvaluationCase { Id = "f1", Source = CaseSources.Failure, Kind = "organize", Instruction = "delete project X", Expect = new Expectation { ForbiddenActions = ["delete_project"] }, Why = "deletion is direct only" },
        ]);
        Assert.Empty(complete.Validate());
        Assert.Equal(complete.Sha256, EvaluationSet.Parse(complete.ToJson()).Sha256);                                                             // the set hash survives a JSON round trip
    }

    [Fact]
    public void TheAuthoredSetsAreWellFormedAndHeldOut()
    {
        var authored = Authored();
        Assert.True(authored.Cases.Count >= 15, $"expected the authored sets, found {authored.Cases.Count} case(s) in {CasesDirectory}");
        Assert.NotEmpty(authored.Of(CaseSources.Unseen));
        Assert.NotEmpty(authored.Of(CaseSources.Failure));
        Assert.Empty(authored.Validate());
        Assert.All(authored.Of(CaseSources.Failure), c => Assert.False(string.IsNullOrWhiteSpace(c.Why)));
        // Both stages are covered by authored cases.
        Assert.Contains(authored.Cases, c => c.IsJudgeCase);
        Assert.Contains(authored.Cases, c => !c.IsJudgeCase);
    }

    // ----------------------------------------------------------------------------------------
    // Running the harness
    // ----------------------------------------------------------------------------------------

    /// <summary>The whole harness: recorded cases from real task diagnostics plus the authored sets, scored against the rules planner and the heuristic judge.</summary>
    [Fact]
    public async Task TheRulesPlannerAndTheHeuristicJudgePassTheWholeSet()
    {
        using var s = World(_tmp);
        var sessionStart = s.H.Clock.UtcNow;

        // Record a session's worth of tasks; each finished task is a diagnostics file.
        s.Command("what did we decide about the Atlas beta date?").ExpectState(RelayState.Completed).ExpectAnswerContains("October 14")
         .Command("list projects").ExpectState(RelayState.Completed)
         .Command("research Lightshift's competitors and give me an implementation plan").ExpectState(RelayState.Completed).ExpectNoProposals()
         .Command("move the backyard notes into Garden").ExpectState(RelayState.AwaitingApproval)
         .Reject(Actions.CreateProject).Reject().Reject().Reject().ExpectState(RelayState.Completed).ExpectOutcome("rejected")
         .Command("keep responses concise").ApproveAll().ExpectState(RelayState.Completed).ExpectEvent(EventTypes.ChangeSetApplied, 2);
        // The self-change is a change set: revert it, so the record is back where the recorded cases and the authored cases expect it.
        foreach (var change in s.Snap.ChangeSets.Where(c => !c.Reverted).ToList()) s.Do($"Revert {change.Kind}", c => Assert.True(c.RevertChangeSet(change.ChangeSetId)));
        Assert.Null(s.H.SelfChange.PromptFragment("planner"));

        // The tasks that built the world (creating projects, filing the inbox) are not cases against that world; the session's are.
        var everything = RecordedCases.Load(s.H.Root.TasksDirectory);
        Assert.Contains(everything, c => c.Instruction == "create project Atlas" && c.Expect.Actions is [Actions.CreateProject]);
        var recorded = RecordedCases.Load(s.H.Root.TasksDirectory, since: sessionStart);
        Assert.Equal(5, recorded.Count);
        Assert.DoesNotContain(recorded, c => c.Instruction.StartsWith("create project", StringComparison.Ordinal));
        Assert.Contains(recorded, c => c.Instruction == "keep responses concise" && c.Expect.Actions is { Count: 2 });
        Assert.Contains(recorded, c => c.Instruction.StartsWith("research", StringComparison.Ordinal) && c.Expect.CapabilityGap == true && c.Expect.Actions is { Count: 0 });
        Assert.Contains(recorded, c => c.Instruction.StartsWith("move the backyard", StringComparison.Ordinal) && c.Expect.Actions is null && c.Why!.Contains("rejected by the user"));
        Assert.All(recorded, c => Assert.Equal(CaseSources.Recorded, c.Source));

        var set = Authored().With(recorded);
        Assert.Empty(set.Validate());

        var runner = new EvaluationRunner(new RuleBasedOrchestrator(), _ => s.H.PlannerContext(), new HeuristicJudge(), WorldJudgeContext, () => s.H.Clock.UtcNow);
        var report = await runner.RunAsync(set, CancellationToken.None);

        Assert.True(report.Passed, report.Render());
        Assert.Equal(set.Cases.Count, report.Results.Count);
        Assert.Equal(3, report.Scores.Count);                                                                                                  // recorded, unseen, failure all ran
        Assert.All(report.Scores, sc => Assert.Equal(sc.Total, sc.Passed));
        Assert.Contains(report.Results, r => r.Stage == "judge" && r.Producer == HeuristicJudge.ProducerName);
        Assert.Contains(report.Results, r => r.Stage == "plan" && r.Producer == "rules");
        Assert.Equal(set.Sha256, report.SetSha256);
        Assert.Contains("PASS", report.Render());
        Assert.Contains("\"setSha256\"", report.ToJson());

        // Nothing the evaluation did touched the record: plans were scored, never executed.
        Assert.Equal(2, s.H.Registry.Active.Count());
        Assert.Null(s.H.Registry.FindActive("garden"));
        Assert.Null(s.H.Registry.FindActive("lighthouse"));
        Assert.Empty(s.H.Preferences.Compiled().WatchedTerms.Where(t => t == "OKR"));
    }

    [Fact]
    public async Task AFailingCaseIsReportedWithItsReasonsAndWhatWasObserved()
    {
        using var s = World(_tmp);
        var authored = Authored();

        // A planner that answers everything with a shrug and proposes nothing.
        var shrug = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "shrug", [], "I do not know.", [], [], "canned"));
        var report = await new EvaluationRunner(shrug, _ => s.H.PlannerContext(), new HeuristicJudge(), WorldJudgeContext).RunAsync(authored, CancellationToken.None);
        Assert.False(report.Passed);
        var create = Assert.Single(report.Results, r => r.Id == "unseen:create-project-phrasing");
        Assert.False(create.Passed);
        Assert.Contains(create.Failures, f => f.Contains("Expected proposals [create_project] but got []"));
        Assert.Contains(create.Failures, f => f.Contains("Target assertion 'create_project.name=Lighthouse': no 'create_project' proposal"));
        Assert.Contains("answer(14): I do not know.", create.Observed);
        var research = Assert.Single(report.Results, r => r.Id == "unseen:research-without-profile");
        Assert.Contains(research.Failures, f => f.Contains("Expected a stated knowledge gap"));
        Assert.Contains(research.Failures, f => f.Contains("does not mention 'Missing:'"));
        // Failure cases that only forbid actions still pass for a planner that proposes nothing; judge cases are untouched by the planner.
        Assert.True(report.Results.Single(r => r.Id == "failure:overheard-wish-is-not-a-self-change").Passed);
        Assert.True(report.Results.Single(r => r.Id == "unseen:judge-watched-term").Passed);
        var rendered = report.Render();
        Assert.Contains("FAIL", rendered);
        Assert.Contains("FAILED unseen:create-project-phrasing [unseen, plan] by canned", rendered);
        Assert.Contains("- Expected proposals [create_project] but got []", rendered);
        Assert.Contains("observed: understood; no proposals; answer(14): I do not know.", rendered);

        // A planner that throws, and one that never answers, fail their cases instead of ending the run.
        var throwing = await new EvaluationRunner(new ThrowingOrchestrator(), _ => s.H.PlannerContext(), new HeuristicJudge(), WorldJudgeContext).RunAsync(authored, CancellationToken.None);
        Assert.All(throwing.Results.Where(r => r.Stage == "plan"), r => Assert.Contains(r.Failures, f => f.Contains("threw InvalidOperationException: model exploded")));
        Assert.All(throwing.Results.Where(r => r.Stage == "judge"), r => Assert.True(r.Passed));
        var hanging = new EvaluationRunner(new HangingOrchestrator(), _ => s.H.PlannerContext(), new HeuristicJudge(), WorldJudgeContext) { CaseTimeout = TimeSpan.FromMilliseconds(50) };
        var subset = new EvaluationSet(authored.Of(CaseSources.Unseen).Where(c => !c.IsJudgeCase).Take(2).Concat(authored.Of(CaseSources.Failure).Where(c => !c.IsJudgeCase).Take(1)));
        Assert.Empty(subset.Validate());
        var timedOut = await hanging.RunAsync(subset, CancellationToken.None);
        Assert.Equal(3, timedOut.Results.Count);
        Assert.All(timedOut.Results, r => Assert.Contains(r.Failures, f => f.Contains("did not answer within")));

        // A judge case without a judge is a failure, not a crash.
        var noJudge = await new EvaluationRunner(shrug, _ => s.H.PlannerContext()).RunAsync(authored, CancellationToken.None);
        Assert.Contains(noJudge.Results, r => r.Stage == "judge" && !r.Passed && r.Failures[0].Contains("No judge was given"));
    }

    [Fact]
    public void ScoringReadsTargetAssertionsIncludingDottedActionNames()
    {
        var plan = new TurnPlan(true, "x", [], "Knowledge state: Missing: a. Capability: none.", [],
            [new Proposal("P1", Actions.ModelRequest, "r", new Dictionary<string, string> { ["profile"] = "research", ["allowSearch"] = "true" }, ["E1"], [], Risks.External, true, Producers.Rules)], "rules",
            Knowledge: new KnowledgeState([], ["a"], true, "gap"));
        Assert.Empty(EvaluationRunner.Score(new Expectation { Actions = ["model.request"], Targets = ["model.request.profile=research", "model.request.allowSearch"], KnowledgeGap = true, CapabilityGap = true, MaxAnswerChars = 100 }, plan));
        var failures = EvaluationRunner.Score(new Expectation { Targets = ["model.request.profile=other", "model.request.budgetTokens", "nonsense"], MaxAnswerChars = 10, Consistent = true, ForbiddenActions = ["model.request"] }, plan);
        Assert.Contains(failures, f => f.Contains("'model.request.profile=other' does not hold"));
        Assert.Contains(failures, f => f.Contains("'model.request.budgetTokens' does not hold"));
        Assert.Contains(failures, f => f.Contains("Malformed target assertion 'nonsense'"));
        Assert.Contains(failures, f => f.Contains("at most 10 were allowed"));
        Assert.Contains(failures, f => f.Contains("Expected consistent=true but the plan says undetermined"));
        Assert.Contains(failures, f => f.Contains("'model.request' must not be proposed here"));
    }

    /// <summary>Runs only when RELAY_LIVE_MODEL_KEY is set (optionally RELAY_LIVE_MODEL_ENDPOINT / RELAY_LIVE_MODEL): the same cases score a real model. Never runs in CI.</summary>
    [Fact]
    public async Task LiveModelIsScoredAgainstTheSameCases()
    {
        var key = Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        var settings = new ModelSettings
        {
            Enabled = true,
            Endpoint = Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL_ENDPOINT") ?? "https://api.openai.com/v1/chat/completions",
            Model = Environment.GetEnvironmentVariable("RELAY_LIVE_MODEL") ?? "gpt-4o-mini",
            SecretName = "live",
            TimeoutMs = 60_000,
        };
        var secrets = new MemorySecretStore();
        secrets.Set("live", key);
        using var client = new OpenAiCompatibleClient(settings, secrets);
        var planner = new CompositeOrchestrator(new RuleBasedOrchestrator(), new ModelOrchestrator(client));
        using var s = World(_tmp, planner, cfg => { cfg.Orchestrator.Mode = OrchestratorSettings.RulesAndModel; cfg.Model.Enabled = true; cfg.Model.Endpoint = settings.Endpoint; cfg.Model.Model = settings.Model; });

        // The model alone, so the grammar does not answer for it.
        var report = await new EvaluationRunner(new ModelOrchestrator(client), _ => s.H.PlannerContext(), new HeuristicJudge(), WorldJudgeContext).RunAsync(Authored(), CancellationToken.None);
        var path = Path.Combine(s.H.Root.Path, "evaluation-live.json");
        File.WriteAllText(path, report.ToJson());
        Assert.Empty(report.Problems);
        Assert.NotEmpty(report.Results);
        Assert.True(report.Results.Count(r => r.Stage == "plan" && r.Passed) > 0, report.Render());
    }

    // ----------------------------------------------------------------------------------------

    private static TurnPlan Plan(Harness h, string instruction)
    {
        var request = new TurnRequest("T1", "C1", "E1", instruction, h.Clock.UtcNow, TaskOrigin.Direct, TaskKind.Improve);
        return new RuleBasedOrchestrator().PlanAsync(request, h.PlannerContext(), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>A planner that never answers: it honours cancellation and nothing else.</summary>
    private sealed class HangingOrchestrator : IOrchestrator
    {
        public string Name => "hanging";
        public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<TurnPlan>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }
}
