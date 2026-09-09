using Relay.Core.Config;
using Relay.Core.Evaluation;
using Relay.Core.Judge;
using Relay.Core.Ledger;
using Relay.Core.Mind;
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

    /// <summary>The model-primary set: multi-segment windows and delegation cases written for a model judge and a model planner; the grammar is never asked to pass it.</summary>
    private static EvaluationSet ModelPrimary() => EvaluationSet.Load(Path.Combine(CasesDirectory, "model"));

    private static readonly string[] LightshiftNotes =
    [
        "Lightshift is our scheduling app for shift workers in small restaurants.",
        "We decided Lightshift targets independent restaurants first, chains later.",
        "The Lightshift pilot runs at two restaurants, in Leeds and Hull.",
    ];

    private static void WithResearchProfile(RelaySettings s)
        => s.ExternalModels.Add(new ExternalModelProfile { Name = "research", Endpoint = "https://api.example.test/v1/chat/completions", Model = "gpt-5-nano", SecretName = "external-research", SupportsSearch = true });

    /// <summary>The world the model-primary cases are written against: <see cref="World"/> plus Lightshift with its three notes, and a named external profile the planner may delegate to.</summary>
    private static Scenario LiveWorld(TempRoot tmp, IOrchestrator orchestrator, Action<RelaySettings> configure)
    {
        var s = World(tmp, orchestrator, cfg => { configure(cfg); WithResearchProfile(cfg); }).WithSecret("external-research")
            .Command("create project Lightshift").Approve().ExpectProject("lightshift");
        foreach (var text in LightshiftNotes) s.Note(text);
        return s.Command("file all notes under Lightshift").ExpectState(RelayState.Completed);
    }

    /// <summary>What the coordinator hands the judge in that world: projects as "Name (slug)", the watched term, no recent topics.</summary>
    private static JudgeContext LiveJudgeContext(EvaluationCase _) => new(["Atlas (atlas)", "Home (home)", "Lightshift (lightshift)"], ["CAD"], [], null);

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

        var runner = new EvaluationRunner(new RuleBasedOrchestrator(), c => s.H.PlannerContext(c), new HeuristicJudge(), WorldJudgeContext, () => s.H.Clock.UtcNow);
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
        var report = await new EvaluationRunner(shrug, c => s.H.PlannerContext(c), new HeuristicJudge(), WorldJudgeContext).RunAsync(authored, CancellationToken.None);
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
        var throwing = await new EvaluationRunner(new ThrowingOrchestrator(), c => s.H.PlannerContext(c), new HeuristicJudge(), WorldJudgeContext).RunAsync(authored, CancellationToken.None);
        Assert.All(throwing.Results.Where(r => r.Stage == "plan"), r => Assert.Contains(r.Failures, f => f.Contains("threw InvalidOperationException: model exploded")));
        Assert.All(throwing.Results.Where(r => r.Stage == "judge"), r => Assert.True(r.Passed));
        var hanging = new EvaluationRunner(new HangingOrchestrator(), c => s.H.PlannerContext(c), new HeuristicJudge(), WorldJudgeContext) { CaseTimeout = TimeSpan.FromMilliseconds(50) };
        var subset = new EvaluationSet(authored.Of(CaseSources.Unseen).Where(c => !c.IsJudgeCase).Take(2).Concat(authored.Of(CaseSources.Failure).Where(c => !c.IsJudgeCase).Take(1)));
        Assert.Empty(subset.Validate());
        var timedOut = await hanging.RunAsync(subset, CancellationToken.None);
        Assert.Equal(3, timedOut.Results.Count);
        Assert.All(timedOut.Results, r => Assert.Contains(r.Failures, f => f.Contains("did not answer within")));

        // A judge case without a judge is a failure, not a crash.
        var noJudge = await new EvaluationRunner(shrug, c => s.H.PlannerContext(c)).RunAsync(authored, CancellationToken.None);
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

    /// <summary>The judge-stage assertions that make a finding checkable as a task: right kind, right project, grounded in the right words, one task per thing, and a usable prompt and note.</summary>
    [Fact]
    public void ScoringChecksAFindingsProjectGroundingCountAndText()
    {
        string[] ids = ["s1", "s2", "s3"];
        var fence = new JudgeFinding(TaskKind.Remember, 0.9, "Fence gets cedar stain", "decision about materials", "File the decision: 'the backyard fence gets cedar stain, not paint'.", ["s3"], "fence", "Home (home)", Presentation.Ambient, "The backyard fence gets cedar stain, not paint.", "decision");
        var decision = new JudgeDecision([fence], "model:test", 800, 90, 1200);

        Assert.Empty(EvaluationRunner.Score(new Expectation { FindingKind = "remember", FindingProject = "Home", FindingSegments = [3], MaxFindings = 1, MinFindings = 1, PromptContains = ["cedar"], NoteContains = ["fence"] }, decision, ids));
        Assert.Empty(EvaluationRunner.Score(new Expectation { FindingProject = "home" }, decision, ids));                    // by slug, case-insensitively

        var failures = EvaluationRunner.Score(new Expectation { FindingKind = "remember", FindingProject = "Atlas", FindingSegments = [2], MaxFindings = 0, MinFindings = 2, PromptContains = ["October"], NoteContains = ["paint the deck"], FindingNoProject = true }, decision, ids);
        Assert.Contains(failures, f => f.Contains("names the project 'Atlas'") && f.Contains("Home (home)"));
        Assert.Contains(failures, f => f.Contains("cites segment(s) [s2]") && f.Contains("cited: [s3]"));
        Assert.Contains(failures, f => f.Contains("at most 0 finding(s); got 1"));
        Assert.Contains(failures, f => f.Contains("at least 2 finding(s); got 1"));
        Assert.Contains(failures, f => f.Contains("focused prompt mentioning 'October'"));
        Assert.Contains(failures, f => f.Contains("note text mentioning 'paint the deck'"));
        Assert.Contains(failures, f => f.Contains("No finding may name a project here"));
        // Per-finding assertions apply to the findings of the expected kind only: a check finding's project does not satisfy a remember expectation.
        var mixed = decision with { Findings = [fence with { Kind = TaskKind.Check, ProjectHint = "Atlas" }, fence with { ProjectHint = null }] };
        Assert.Contains(EvaluationRunner.Score(new Expectation { FindingKind = "remember", FindingProject = "Atlas" }, mixed, ids), f => f.Contains("No 'remember' finding names the project 'Atlas'"));
        Assert.Empty(EvaluationRunner.Score(new Expectation { FindingKind = "check", FindingProject = "Atlas" }, mixed, ids));
        // Without the window's ids, grounding cannot be checked and says so rather than passing silently.
        Assert.Contains(EvaluationRunner.Score(new Expectation { FindingSegments = [3] }, decision), f => f.Contains("cannot be checked"));
    }

    /// <summary>A judge case marks which segments are new; the runner hands the judge the whole window with only those as NEW, and the guard rejects positions outside the window.</summary>
    [Fact]
    public async Task JudgeCasesTellTheRunnerWhichSegmentsAreNew()
    {
        var requests = new List<JudgeRequest>();
        var spy = new SpyJudge(r => { requests.Add(r); return JudgeDecision.Nothing("spy"); });
        static EvaluationCase Case(string id, IReadOnlyList<int>? fresh, Expectation? expect = null) => new()
        {
            Id = id, Source = CaseSources.Unseen, Origin = "observed", Segments = ["old decision", "old reply", "new chatter"], NewSegments = fresh, Expect = expect ?? new Expectation { Significant = false }, Why = "w",
        };
        var set = new EvaluationSet([Case("unseen:x", [3]), new EvaluationCase { Id = "failure:y", Source = CaseSources.Failure, Origin = "observed", Segments = ["a"], Expect = new Expectation { Significant = false }, Why = "guards" }]);
        Assert.Empty(set.Validate());
        var report = await new EvaluationRunner(new RuleBasedOrchestrator(), _ => throw new InvalidOperationException("no planner cases here"), spy).RunAsync(set, CancellationToken.None);
        Assert.True(report.Passed, report.Render());
        Assert.Equal(2, requests.Count);
        var seen = Assert.Single(requests, r => r.Window.Count == 3);
        Assert.Equal([seen.Window[2].SegmentId], seen.NewSegmentIds);
        Assert.True(seen.At > seen.Window[2].At);
        var everythingNew = Assert.Single(requests, r => r.Window.Count == 1);
        Assert.Equal(everythingNew.Window.Select(w => w.SegmentId), everythingNew.NewSegmentIds);   // a case without newSegments marks everything new

        var outside = new EvaluationSet([Case("unseen:z", [4]), Case("unseen:w", null, new Expectation { FindingKind = "remember", FindingSegments = [0] }), Case("unseen:v", [])]);
        var problems = outside.Validate();
        Assert.Contains(problems, p => p.Contains("'unseen:z'") && p.Contains("position 4 is outside the window (1..3)"));
        Assert.Contains(problems, p => p.Contains("'unseen:w'") && p.Contains("position 0 is outside"));
        Assert.Contains(problems, p => p.Contains("'unseen:v'") && p.Contains("newSegments is empty"));
        var planner = new EvaluationSet([new EvaluationCase { Id = "unseen:p", Source = CaseSources.Unseen, Instruction = "list projects", Expect = new Expectation { Understood = true, FindingProject = "Atlas" } }]);
        Assert.Contains(planner.Validate(), p => p.Contains("'unseen:p'") && p.Contains("cannot expect judge findings"));
    }

    private sealed class SpyJudge(Func<JudgeRequest, JudgeDecision> decide) : IJudge
    {
        public string Name => "spy";
        public Task<JudgeDecision> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken) => Task.FromResult(decide(request));
    }

    /// <summary>
    /// An observed plan case carries the words the judge kept ("heard"); the runner hands the planner an excerpt id and the
    /// world stores exactly those words under it, so the planner's read_excerpt sees what it would see in the application.
    /// </summary>
    [Fact]
    public async Task ObservedPlanCasesGiveThePlannerTheWordsAsAnExcerpt()
    {
        using var s = World(_tmp);
        var requests = new List<TurnRequest>();
        var reader = new ExcerptReadingOrchestrator(requests);
        const string words = "the Atlas beta is going out on October 21 now, not the 14th";
        var heard = new EvaluationCase { Id = "unseen:heard/1", Source = CaseSources.Unseen, Origin = "observed", Kind = "check", Instruction = "Check the stated Atlas beta date against the stored decision.", Heard = words, Expect = new Expectation { Understood = true, AnswerContains = ["October 21"] } };
        var direct = new EvaluationCase { Id = "failure:direct", Source = CaseSources.Failure, Instruction = "list projects", Expect = new Expectation { Understood = true, AnswerContains = ["no excerpt"] }, Why = "a direct ask once carried a stale excerpt" };
        var set = new EvaluationSet([heard, direct]);
        Assert.Empty(set.Validate());

        var report = await new EvaluationRunner(reader, c => s.H.PlannerContext(c)).RunAsync(set, CancellationToken.None);
        Assert.True(report.Passed, report.Render());                                                    // the planner answered with the words it read through read_excerpt
        var id = EvaluationRunner.ExcerptIdFor(heard);
        Assert.Equal("eval-heard-unseen-heard-1", id);                                                   // safe as a file name
        Assert.Equal(id, Assert.Single(requests, r => r.TurnId == "eval-unseen:heard/1").ExcerptId);
        Assert.Null(Assert.Single(requests, r => r.TurnId == "eval-failure:direct").ExcerptId);         // a direct ask carries no excerpt
        Assert.Equal(words, s.H.Excerpts.Read(id)?.Text);
        Assert.Equal(1, s.H.Excerpts.Count);

        // Misuse is a validation problem, not a silent no-op.
        var problems = new EvaluationSet([
            new EvaluationCase { Id = "unseen:h2", Source = CaseSources.Unseen, Origin = "direct", Instruction = "x", Heard = words, Expect = new Expectation { Understood = true } },
            new EvaluationCase { Id = "unseen:h3", Source = CaseSources.Unseen, Origin = "observed", Segments = ["a"], Heard = words, Expect = new Expectation { Significant = false } },
            new EvaluationCase { Id = "unseen:h4", Source = CaseSources.Unseen, Origin = "observed", Instruction = "x", Heard = "  ", Expect = new Expectation { Understood = true } },
        ]).Validate();
        Assert.Equal(3, problems.Count(p => p.Contains("heard is the excerpt of an observed plan case")));
    }

    /// <summary>A planner that does what the model planner does first for an observed task: read_excerpt through the broker, then answer with the words.</summary>
    private sealed class ExcerptReadingOrchestrator(List<TurnRequest> requests) : IOrchestrator
    {
        public string Name => "excerpt-reader";
        public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
        {
            requests.Add(request);
            string answer = "no excerpt";
            if (request.ExcerptId is not null)
            {
                var result = context.Tools.Call("read_excerpt", new Dictionary<string, string> { ["excerptId"] = request.ExcerptId });
                answer = result.Ok ? result.Hits![0].Text : result.Error ?? "failed";
            }
            return Task.FromResult(new TurnPlan(true, "read the excerpt", [], answer, [], [], Name));
        }
    }

    /// <summary>
    /// The model-primary set is complete on its own, is written only for a model (every case is prefixed so the report
    /// names the set), and every judge case says which segments are new and cites positions inside its window.
    /// </summary>
    [Fact]
    public void TheModelPrimarySetIsCompleteAndAddressesBothStages()
    {
        var set = ModelPrimary();
        Assert.True(set.Cases.Count >= 25, $"expected the model-primary sets, found {set.Cases.Count} case(s) under {Path.Combine(CasesDirectory, "model")}");
        Assert.Empty(set.Validate());
        Assert.All(set.Cases, c => Assert.StartsWith("model:", c.Id));
        Assert.All(set.Cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Why), $"{c.Id} does not say why it exists"));
        var judge = set.Cases.Where(c => c.IsJudgeCase).ToList();
        var plan = set.Cases.Where(c => !c.IsJudgeCase).ToList();
        Assert.True(judge.Count >= 15 && plan.Count >= 8, $"judge {judge.Count}, plan {plan.Count}");
        // Every task kind the judge can name is asked for at least once, and silence is asked for too.
        foreach (var kind in new[] { "remember", "check", "resolve", "answer", "organize", "research" })
            Assert.Contains(judge, c => c.Expect.FindingKind == kind);
        Assert.Contains(judge, c => c.Expect.Significant == false);
        Assert.Contains(judge, c => c.Expect.ForbiddenFindingKinds is { Count: > 0 });
        // Context the judge has already seen is part of the windows: some cases mark only the tail as new.
        Assert.Contains(judge, c => c.NewSegments is { } n && n.Count < c.Segments!.Count);
        // Delegation to other agents appears on both sides: asked for where it belongs, forbidden where it does not.
        Assert.Contains(plan, c => c.Expect.Actions is { } a && a.Contains(Actions.LaunchWorker));
        Assert.Contains(plan, c => c.Expect.Actions is { } a && a.Contains(Actions.ModelRequest));
        Assert.Contains(plan, c => c.Expect.ForbiddenActions is { } f && f.Contains(Actions.ModelRequest) && c.ParsedOrigin == TaskOrigin.Observed);
        // Combined with the authored set it is still one valid evaluation set.
        Assert.Empty(Authored().With(set.Cases).Validate());
    }

    /// <summary>
    /// Runs only when RELAY_LIVE_MODEL_KEY is set (with RELAY_LIVE_MODEL_ENDPOINT / RELAY_LIVE_MODEL; RELAY_LIVE_REPORT_DIR
    /// keeps the report). The model is the primary tool on both stages: the model judge scores every judge case and the
    /// model planner scores every plan case, with no grammar or heuristic in front of either. Accuracy is reported, not
    /// asserted; what is asserted is that the run happened with the model alone. Never runs in CI.
    /// </summary>
    [Fact]
    public async Task LiveModelJudgeAndPlannerAreScoredAsThePrimaryTools()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        using var s = LiveWorld(_tmp, new CompositeOrchestrator(new RuleBasedOrchestrator(), new ModelOrchestrator(client)), live.Configure);
        var set = Authored().With(ModelPrimary().Cases);
        Assert.Empty(set.Validate());

        var runner = new EvaluationRunner(new ModelOrchestrator(client), c => s.H.PlannerContext(c), new ModelJudge(client), LiveJudgeContext) { CaseTimeout = TimeSpan.FromSeconds(180) };
        var report = await runner.RunAsync(set, CancellationToken.None);
        live.Write("evaluation-live.json", report.ToJson());
        live.Write("evaluation-live.txt", report.Render());

        Assert.Empty(report.Problems);
        Assert.Equal(set.Cases.Count, report.Results.Count);
        Assert.All(report.Results, r => Assert.StartsWith("model:", r.Producer));                       // nothing but the model produced a verdict
        Assert.Contains(report.Results, r => r.Stage == "judge" && r.Passed);
        Assert.Contains(report.Results, r => r.Stage == "plan" && r.Passed);
        // Nothing the evaluation did touched the record: plans were scored, never executed.
        Assert.Equal(3, s.H.Registry.Active.Count());
        Assert.Null(s.H.Registry.FindActive("garden"));
    }

    // ----------------------------------------------------------------------------------------
    // The mind stage (docs/09): cases scored on the loop's moves
    // ----------------------------------------------------------------------------------------

    /// <summary>The mind set: the world-clock build, local-first answers, a proposal, a delegation, and the failure that pins local-first.</summary>
    private static EvaluationSet MindSet() => EvaluationSet.Load(Path.Combine(CasesDirectory, "mind"));

    /// <summary>The world the mind cases are written against: <see cref="World"/> with a research profile the mind may delegate to.</summary>
    private static Scenario MindWorld(TempRoot tmp)
    {
        var s = Scenario.New(tmp, WithResearchProfile, externalClients: _ => new ScriptedModelClient()).WithWorkspace()
            .Command("create project Atlas").Approve().ExpectProject("atlas")
            .Command("create project Home").Approve().ExpectProject("home")
            .Note("We decided the Atlas beta ships on October 14.");
        foreach (var text in BackyardNotes) s.Note(text);
        return s.Command("file all notes under Home").ExpectState(RelayState.Completed);
    }

    /// <summary>A scripted mind that makes the right moves for every case: reads notes with a tool before answering, builds when no tool can answer, proposes and delegates where those belong.</summary>
    private static ScriptedMind CorrectMind() => new ScriptedMind().Always(req =>
    {
        var ask = req.Transcript.OfType<InputObserved>().First().Text;
        var last = req.Transcript[^1];
        if (last is ToolObserved tool)
        {
            var text = tool.Data ?? tool.Summary;
            var answer = ask.Contains("projects", StringComparison.OrdinalIgnoreCase) ? "You have two projects: Atlas and Home."
                : text.Contains("October 14", StringComparison.Ordinal) ? "The Atlas beta ships on October 14."
                : text.Contains("spring", StringComparison.OrdinalIgnoreCase) ? "The fence gets replaced in spring."
                : "I found nothing about that in your notes.";
            return MindStep.Of(ScriptedMind.Say(answer), "Answered from your notes", ScriptedMind.Read(0.1, MindRead.NeedLocalNotes));
        }
        if (ask.Contains("time is it", StringComparison.OrdinalIgnoreCase))
            return MindStep.Of(ScriptedMind.Build("world_clock", "No tool tells the time in another zone.", "zone", "local time"), "Relay has no clock; building one", ScriptedMind.Read(0.4, MindRead.NeedNewTool));
        if (ask.Contains("Which projects", StringComparison.OrdinalIgnoreCase))
            return MindStep.Of(ScriptedMind.Tool("list_projects"), "Listing your projects", ScriptedMind.Read(0.1, MindRead.NeedLocalNotes));
        if (ask.StartsWith("Start a new project", StringComparison.OrdinalIgnoreCase))
            return MindStep.Of(ScriptedMind.Propose(Actions.CreateProject, "You asked for a project", ("name", "Garden Redesign")), "Proposing project Garden Redesign", ScriptedMind.Read(0.2));
        if (ask.StartsWith("Research", StringComparison.OrdinalIgnoreCase))
            return MindStep.Of(ScriptedMind.Delegate("research", "Summarise how UK councils license scheduling software pilots for restaurants."), "Asking research", ScriptedMind.Read(0.8, MindRead.NeedWorldKnowledge, MindRead.NeedExternalReasoning));
        return MindStep.Of(ScriptedMind.Tool("search", ("query", ask.Contains("fence", StringComparison.OrdinalIgnoreCase) ? "backyard fence" : "Atlas beta")), "Searching your notes", ScriptedMind.Read(0.2, MindRead.NeedLocalNotes));
    });

    [Fact]
    public void ASearchFilteredToTheWrongProjectWidensAndSaysSo()
    {
        using var s = MindWorld(_tmp);
        var tools = s.H.PlannerContext().Tools;
        var narrow = tools.Call("search", new Dictionary<string, string> { ["query"] = "backyard fence", ["project"] = "atlas" });
        Assert.True(narrow.Ok);
        Assert.StartsWith("0 hit(s) for \"backyard fence\" in project 'atlas'; ", narrow.Summary);
        Assert.Contains("hit(s) in other projects (home), listed below", narrow.Summary);
        Assert.NotEmpty(narrow.Hits!);
        Assert.All(narrow.Hits!, h => Assert.Equal("home", h.ProjectSlug));
        // A filter that finds something, and a query that is nowhere, keep the plain summary.
        Assert.StartsWith("1 hit(s) for \"October 14\"", tools.Call("search", new Dictionary<string, string> { ["query"] = "October 14", ["project"] = "atlas" }).Summary);
        Assert.Equal("0 hit(s) for \"zeppelin\"", tools.Call("search", new Dictionary<string, string> { ["query"] = "zeppelin", ["project"] = "atlas" }).Summary);
    }

    [Fact]
    public async Task MindCasesScoreTheLoopsMovesAndStopAtTheFirstThingThatNeedsTheUser()
    {
        using var s = MindWorld(_tmp);
        var set = MindSet();
        Assert.Empty(set.Validate());
        Assert.All(set.Cases, c => Assert.True(c.IsMindCase));
        Assert.Empty(Authored().With(set.Cases).Validate());                                           // one valid set together with the planner and judge cases

        var runner = new EvaluationRunner(new RuleBasedOrchestrator(), c => s.H.PlannerContext(c), mind: CorrectMind(), mindContext: _ => s.H.MindContext(), clock: () => s.H.Clock.UtcNow);
        var report = await runner.RunAsync(set, CancellationToken.None);
        Assert.True(report.Passed, report.Render());
        Assert.Equal(set.Cases.Count, report.Results.Count);
        Assert.All(report.Results, r => { Assert.Equal("mind", r.Stage); Assert.Equal("mind:scripted", r.Producer); });
        var clock = Assert.Single(report.Results, r => r.Id == "unseen:mind/world-clock");
        Assert.Contains("moves: build:world_clock", clock.Observed);
        Assert.Contains("outcome: build", clock.Observed);
        Assert.Contains("needs=[new_tool]", clock.Observed);
        // Nothing was executed or sent: the world is as it was.
        Assert.Equal(2, s.H.Registry.Active.Count());
        Assert.Null(s.H.Registry.FindActive("garden-redesign"));
        Assert.Equal(0, s.H.Count(EventTypes.ExternalPackaged));

        // A mind that sends everything to another AI fails the local-first cases with readable reasons; a runner without a mind fails them all.
        var outsourcer = new ScriptedMind().Always(_ => MindStep.Of(ScriptedMind.Delegate("research", "Answer this."), "Delegating", ScriptedMind.Read(0.9, MindRead.NeedExternalReasoning)));
        var lazy = await new EvaluationRunner(new RuleBasedOrchestrator(), c => s.H.PlannerContext(c), mind: outsourcer, mindContext: _ => s.H.MindContext()).RunAsync(set, CancellationToken.None);
        Assert.False(lazy.Passed);
        var fence = Assert.Single(lazy.Results, r => r.Id == "failure:mind/local-fact-not-delegated");
        Assert.Contains(fence.Failures, f => f.Contains("delegate must not be made here"));
        Assert.Contains(fence.Failures, f => f.Contains("Expected the outcome 'answered'; it was 'approval'"));
        var world = Assert.Single(lazy.Results, r => r.Id == "unseen:mind/world-clock");
        Assert.Contains(world.Failures, f => f.Contains("Expected the first read to need 'new_tool'"));
        Assert.Contains(world.Failures, f => f.Contains("missing build:*"));
        var mindless = await new EvaluationRunner(new RuleBasedOrchestrator(), c => s.H.PlannerContext(c)).RunAsync(set, CancellationToken.None);
        Assert.All(mindless.Results, r => Assert.Contains(r.Failures, f => f.Contains("No mind was given")));
    }

    /// <summary>
    /// Runs only when RELAY_LIVE_MODEL_KEY is set. RELAY0's model is the mind: every mind case is stepped by the schema-constrained
    /// model over the same world, and the moves it makes are scored. Accuracy is reported, not asserted; what is asserted is that
    /// the model alone made every move and nothing was executed. Never runs in CI.
    /// </summary>
    [Fact]
    public async Task LiveModelMindIsScoredOnItsMoves()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        using var s = MindWorld(_tmp);
        var set = MindSet();
        Assert.Empty(set.Validate());

        var mind = new ModelMind(client);
        var runner = new EvaluationRunner(new RuleBasedOrchestrator(), c => s.H.PlannerContext(c), mind: mind, mindContext: _ => s.H.MindContext()) { CaseTimeout = TimeSpan.FromSeconds(240) };
        var report = await runner.RunAsync(set, CancellationToken.None);
        live.Write("mind-live.json", report.ToJson());
        live.Write("mind-live.txt", report.Render());

        Assert.Empty(report.Problems);
        Assert.Equal(set.Cases.Count, report.Results.Count);
        Assert.All(report.Results, r => Assert.StartsWith("mind:", r.Producer));
        Assert.Equal(2, s.H.Registry.Active.Count());
        Assert.Equal(0, s.H.Count(EventTypes.ExternalPackaged));
    }

    private const string ExternalAnswer =
        "Councils in England do not license scheduling software as such; a restaurant pilot needs no separate council licence. " +
        "Hull City Council's requirements attach to the premises (food business registration, alcohol licence), not to workforce tools. " +
        "Plan: confirm with Hull's licensing team in writing, keep the MSA with the restaurant, and add a data-protection notice for staff. " +
        "Limits: I could not verify whether the restaurant's existing premises licence carries conditions on staff systems.";

    /// <summary>
    /// The live chain with the model in both seats: words are overheard, the model judge raises the tasks, the model planner
    /// reads the record and proposes, policy holds the proposals for approval, and a direct research ask is delegated to a
    /// named external agent (scripted here: it is the counterparty, not what is under test). Runs only with RELAY_LIVE_MODEL_KEY.
    /// The invariants are asserted; the transcript, written to RELAY_LIVE_REPORT_DIR, shows what the model made of each sentence.
    /// </summary>
    [Fact]
    public void LiveModelJudgeRaisesObservedTasksAndTheModelPlannerActsOnThem()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        var external = new ScriptedModelClient().Always(_ => ExternalAnswer);
        // The world is built by the grammar (deterministic, fast); from the first overheard word on, the model plans alone.
        var planner = new SwitchableOrchestrator(new CompositeOrchestrator(new RuleBasedOrchestrator(), new ModelOrchestrator(client)));
        using var s = Scenario.New(_tmp, cfg => { live.Configure(cfg); WithResearchProfile(cfg); }, orchestrator: planner, judge: new ModelJudge(client), externalClients: _ => external, inlinePost: false)
            .WithWorkspace().WithSecret("external-research")
            .Command("create project Atlas").Approve().ExpectProject("atlas")
            .Command("create project Home").Approve().ExpectProject("home")
            .Command("create project Lightshift").Approve().ExpectProject("lightshift")
            .Note("We decided the Atlas beta ships on October 14.").Command("file all notes under Atlas").ExpectState(RelayState.Completed)
            .Note("We decided the backyard fence gets replaced in spring.").Command("file all notes under Home").ExpectState(RelayState.Completed);
        foreach (var text in LightshiftNotes) s.Note(text);
        s.Command("file all notes under Lightshift").ExpectState(RelayState.Completed);
        planner.Current = new ModelOrchestrator(client);

        var wait = TimeSpan.FromSeconds(240);
        Scenario Overhear(string text)
        {
            s.Hear(text).Observe()
             .PumpUntil("the judge pass to finish", () => s.Snap.Listening is null or { Judging: false }, wait)
             .PumpUntil("the tasks it raised to settle", () => !s.Snap.Tasks.Any(t => t.Status is TaskStatus.Planning or TaskStatus.Executing), wait);
            return s;
        }

        try
        {
            s.WithListening(JudgeSettings.Model).StartListening().ExpectListening();
            Overhear("Morning. Did anyone catch the game last night?");
            Overhear("Quick correction on the date, the Atlas beta is going out on October 21 now, not the 14th.");
            Overhear("Someone needs to find out whether Hull council requires a separate licence for the Lightshift pilot.");
            Overhear("Okay, decision made, the backyard fence gets cedar stain, not paint.");
            s.StopListening()
             .PumpUntil("the stream to close", () => s.Snap.Listening is null, wait)
             .PumpUntil("the last tasks to settle", () => !s.Snap.Tasks.Any(t => t.Status is TaskStatus.Planning or TaskStatus.Executing), wait);

            // The judge, not a heuristic, decided every pass; the ledger holds fingerprints of the room, never its words.
            var passes = s.H.Records().Where(r => r.Type is EventTypes.ObserveFound or EventTypes.ObserveChecked).ToList();
            Assert.NotEmpty(passes);
            Assert.All(passes, r => Assert.StartsWith("model:", r.DataString("judge")));
            s.ExpectNoEvent(EventTypes.ObserveFailed);
            var ledger = s.H.LedgerText();
            foreach (var words in new[] { "cedar stain", "Hull council", "October 21", "catch the game" }) Assert.DoesNotContain(words, ledger);

            // Something overheard became work; every task that needed a planner got the model. What the model made of each sentence is the report's business.
            var observed = s.Snap.Tasks.Where(t => t.Origin == TaskOrigin.Observed).ToList();
            Assert.NotEmpty(observed);
            Assert.All(observed.Where(t => t.Kind != TaskKind.Remember || t.Producer != Producers.Judge), t => Assert.StartsWith("model:", t.Producer));
            // Nothing overheard may leave the machine, change Relay, or delete anything.
            foreach (var p in observed.SelectMany(t => t.Proposals))
                Assert.DoesNotContain(p.Action, new[] { Actions.ModelRequest, Actions.UpdatePreference, Actions.UpdatePrompt, Actions.DeleteProject });

            // A direct research ask: the model states the gap and packages the delegation; approval sends exactly that package to the external agent.
            s.Command("research how UK councils license scheduling software pilots for restaurants and give me a plan for the Lightshift pilot")
             .PumpUntil("the research plan", () => s.Snap.State is RelayState.AwaitingApproval or RelayState.Completed, wait);
            if (s.Snap.PendingProposals.Any(p => p.Action == Actions.ModelRequest))
            {
                Assert.Empty(external.Requests);                                                            // nothing has left the machine before approval
                s.Approve(Actions.ModelRequest)
                 .PumpUntil("the external response to be stored", () => s.H.Count(EventTypes.ArtifactStored) > 0 || s.H.Count(EventTypes.TaskFailed) > 0, wait)
                 .PumpUntil("the follow-up to settle", () => !s.Snap.Tasks.Any(t => t.Status is TaskStatus.Planning or TaskStatus.Executing), wait);
                Assert.Single(external.Requests);
                s.ExpectEvent(EventTypes.ExternalPackaged);
            }
        }
        finally
        {
            live.Write("listening-live.txt", s.Transcript());
            live.Write("listening-live.ledger.jsonl", s.H.LedgerText());
            live.Write("listening-live.tasks.json", System.Text.Json.JsonSerializer.Serialize(s.Snap.Tasks.OrderBy(t => t.StartedAt).Select(t => new
            {
                t.TaskId, Origin = t.Origin.Wire(), Kind = t.Kind.Wire(), Status = t.Status.ToString(), t.Producer, t.Title, t.Why, t.Confidence, t.Summary, t.Outcome, t.Answer, t.Consistent,
                Excerpt = t.ExcerptId is null ? null : s.H.Excerpts.Read(t.ExcerptId)?.Text,
                Presentation = t.Presentation.ToString(), t.PresentationReason,
                Knowledge = new { t.Knowledge.Missing, t.Knowledge.CapabilityGap },
                Steps = t.Steps, ToolCalls = t.ToolCalls.Select(c => new { c.Tool, c.Args, c.Ok, c.Summary }),
                ModelCalls = t.ModelCalls.Select(m => new { m.Model, m.Ok, m.PromptTokens, m.CompletionTokens, m.ElapsedMs }),
                Proposals = t.Proposals.Select(p => new { p.Action, p.Status, p.Title, p.Tier, Target = p.Target, p.Reasons }),
                Citations = t.Citations.Select(c => new { c.Kind, c.Id, c.ProjectSlug, c.Excerpt }),
                Cost = new { t.Cost.ModelCalls, t.Cost.TotalTokens, t.Cost.ToolCalls, t.Cost.WallMs },
            }), Relay.Core.Storage.RelayJson.Indented));
        }
    }

    // ----------------------------------------------------------------------------------------

    private static TurnPlan Plan(Harness h, string instruction)
    {
        var request = new TurnRequest("T1", "C1", "E1", instruction, h.Clock.UtcNow, TaskOrigin.Direct, TaskKind.Improve);
        return new RuleBasedOrchestrator().PlanAsync(request, h.PlannerContext(), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>A planner whose implementation can be swapped mid-scenario: the grammar builds the world, then the model alone plans what follows.</summary>
    private sealed class SwitchableOrchestrator(IOrchestrator initial) : IOrchestrator
    {
        public IOrchestrator Current { get; set; } = initial;
        public string Name => Current.Name;
        public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken) => Current.PlanAsync(request, context, cancellationToken);
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
