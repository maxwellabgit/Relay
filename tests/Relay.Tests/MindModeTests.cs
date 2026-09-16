using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Model;
using Relay.Core.Policy;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>
/// Mind mode end to end through the coordinator (docs/09 slice 1): the loop runs a task, the deterministic
/// engine owns every consequence, and each consequence comes back to the mind as an observation — tool
/// results, policy verdicts, the user's approval or refusal, a delegate's partial and final reply.
/// </summary>
public class MindModeTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public MindModeTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static void MindMode(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    private static void WithResearchProfile(RelaySettings s)
        => s.ExternalModels.Add(new ExternalModelProfile { Name = "research", Endpoint = "https://api.example.test/v1/chat/completions", Model = "gpt-5-nano", SecretName = "external-research" });

    [Fact]
    public void AskRunsTheLoopToolResultIsObservedAndTheFeedIsTheTaskPlan()
    {
        var mind = new ScriptedMind()
            .Step(Tool("list_projects"), "Looking at your projects", Read(0.1))
            .Then(req =>
            {
                var tool = Assert.IsType<ToolObserved>(req.Transcript[^1]);
                Assert.True(tool.Ok, tool.Summary);
                return MindStep.Of(Say("You have no projects yet."), "Answered");
            });
        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Ask("what projects do I have?")
            .ExpectState(RelayState.Ready)
            .ExpectOutcome("answered")
            .ExpectAnswerContains("no projects yet")
            .ExpectEvent(EventTypes.MindStepped, 2)
            .ExpectEvent(EventTypes.LoopEnded)
            .ExpectEvent(EventTypes.UsageRecorded)
            .ExpectEvent(EventTypes.DecisionMade);
        _output.WriteLine(s.Transcript());

        Assert.Equal("mind:scripted", s.Response.Producer);
        Assert.Equal(new[] { "Looking at your projects", "Answered" }, s.Response.Steps);
        Assert.Single(s.Response.ToolCalls);
        Assert.Equal(2, s.Response.ModelCalls.Count);
        // The first request saw only the input; the second saw the move it made and what the tool returned.
        Assert.IsType<InputObserved>(Assert.Single(mind.Requests[0].Transcript));
        Assert.Equal(new[] { "input", "move", "tool" }, mind.Requests[1].Transcript.Select(o => o.Kind));
        Assert.Equal(OrchestratorSettings.Mind, s.H.Last(EventTypes.TurnStarted)!.DataString("mode"));
        Assert.Equal("answered", s.H.Last(EventTypes.LoopEnded)!.DataString("outcome"));
        Assert.True(File.Exists(Path.Combine(_tmp.Root.UsageDirectory, Harness.T0.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl")));
    }

    [Fact]
    public void ProposalNeedsApprovalTheLoopWaitsAndHearsTheApprovalAndTheExecution()
    {
        var mind = new ScriptedMind()
            .Step(Propose(Actions.CreateProject, "You asked for it", ("name", "Harbor")), "Proposing project Harbor", Read(0.2))
            .Then(req =>
            {
                var kinds = req.Transcript.Select(o => o.Kind).ToList();
                Assert.Equal(new[] { "input", "move", "policy", "approval", "executed" }, kinds);
                Assert.Equal(PolicyObserved.NeedsApproval, Assert.IsType<PolicyObserved>(req.Transcript[2]).Outcome);
                Assert.True(Assert.IsType<ApprovalObserved>(req.Transcript[3]).Granted);
                Assert.True(Assert.IsType<ExecutionObserved>(req.Transcript[4]).Ok);
                return MindStep.Of(Say("Harbor is ready."), "Created Harbor");
            });
        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Ask("create a project called Harbor")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .ExpectProposal(Actions.CreateProject, "pending")
            .ExpectEvent(EventTypes.LoopWaiting)
            .ExpectProject("harbor", exists: false);
        Assert.Single(mind.Requests); // nothing was stepped while the user decided
        s.Approve(Actions.CreateProject)
            .ExpectState(RelayState.Ready)
            .ExpectProposal(Actions.CreateProject, "executed")
            .ExpectProject("harbor")
            .ExpectAnswerContains("Harbor is ready")
            .ExpectEvent(EventTypes.LoopResumed);
        _output.WriteLine(s.Transcript());
        Assert.Equal(2, mind.Requests.Count);
        Assert.Equal("mind:scripted", s.H.Last(EventTypes.ProposalReceived)!.DataString("proposedBy"));
    }

    [Fact]
    public void RejectionIsObservedAndTheMindTakesAnotherPath()
    {
        var mind = new ScriptedMind()
            .Step(Propose(Actions.CreateProject, "You asked for it", ("name", "Harbor")), "Proposing project Harbor", Read(0.2))
            .Then(req =>
            {
                var approval = Assert.IsType<ApprovalObserved>(req.Transcript[^1]);
                Assert.False(approval.Granted);
                return MindStep.Of(Say("Understood, nothing was created."), "Left as is");
            });
        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Ask("create a project called Harbor")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .Reject(Actions.CreateProject, "not now")
            .ExpectState(RelayState.Ready)
            .ExpectProject("harbor", exists: false)
            .ExpectAnswerContains("nothing was created");
        _output.WriteLine(s.Transcript());
    }

    [Fact]
    public void PolicyDenialIsObservedInsteadOfEndingTheTask()
    {
        var mind = new ScriptedMind()
            .Step(Propose(Actions.CreateProject, "You asked for it", ("name", "")), "Proposing a project", Read(0.2))
            .Then(req =>
            {
                var policy = Assert.IsType<PolicyObserved>(req.Transcript[^1]);
                Assert.Equal(PolicyObserved.Denied, policy.Outcome);
                Assert.NotEmpty(policy.Reasons);
                return MindStep.Of(Ask("What should the project be called?"), "Need a name");
            })
            .Then(req =>
            {
                var reply = Assert.IsType<UserObserved>(req.Transcript[^1]);
                Assert.Equal("Harbor", reply.Text);
                return MindStep.Of(Say("Got it: Harbor."), "Named");
            });
        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Ask("create a project")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)   // waiting for the user's words, shown as the interim answer
            .ExpectAnswerContains("What should the project be called?")
            .ExpectProposal(Actions.CreateProject, "denied");
        Assert.Empty(s.Snap.PendingProposals);
        // The typed reply goes to the waiting task, not to a new one.
        s.Ask("Harbor").ExpectState(RelayState.Ready).ExpectAnswerContains("Got it: Harbor").ExpectTaskCount(1);
        _output.WriteLine(s.Transcript());
        Assert.Equal(3, mind.Requests.Count);
    }

    [Fact]
    public void ContractFailuresAreRetriedThenTheTaskFailsVisibly()
    {
        var mind = new ScriptedMind().Raw("not json").Raw("{\"move\":{\"type\":\"say\"}}").Raw("still not json");
        using var s = Scenario.New(_tmp, MindMode, mind: mind)
            .Ask("hello")
            .ExpectState(RelayState.Ready)
            .ExpectTask(TaskKind.Answer, TaskStatus.Failed, TaskOrigin.Direct)
            .ExpectEvent(EventTypes.MindFailed, 3)
            .ExpectEvent(EventTypes.TaskFailed);
        _output.WriteLine(s.Transcript());
        Assert.Equal(3, mind.Requests.Count);
        // Each retry told the mind what was wrong with its last reply.
        Assert.Contains(mind.Requests[1].Transcript, o => o is SystemObserved sys && sys.Text.Contains("not usable", StringComparison.Ordinal));
        Assert.Equal("mind_failed", s.H.Last(EventTypes.LoopEnded)!.DataString("outcome"));
    }

    [Fact]
    public void StepBudgetEndsARunawayLoop()
    {
        var mind = new ScriptedMind().Always(_ => MindStep.Of(Tool("list_projects"), "Looking again"));
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); cfg.Orchestrator.MaxSteps = 4; cfg.Orchestrator.MaxToolCalls = 10; }, mind: mind)
            .Ask("loop forever")
            .ExpectState(RelayState.Ready)
            .ExpectTask(TaskKind.Answer, TaskStatus.Failed, TaskOrigin.Direct)
            .ExpectEvent(EventTypes.MindStepped, 4);
        _output.WriteLine(s.Transcript());
        Assert.Equal("step_budget", s.H.Last(EventTypes.LoopEnded)!.DataString("outcome"));
        Assert.Equal(4, mind.Requests.Count);
    }

    [Fact]
    public void DelegationIsAPackageThatNeedsApprovalAndItsReplyStreamsBackAsObservations()
    {
        var external = new ChunkedModelClient(chunks: 4, chunkChars: 500, delayMs: 40);
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Summarize the licensing rules for scheduling software pilots in UK councils."), "Asking research", Read(0.8, MindRead.NeedWorldKnowledge))
            .Always(req =>
            {
                var last = req.Transcript[^1];
                return last switch
                {
                    DelegateObserved { Stage: DelegateObserved.Returned } d => MindStep.Of(Say($"Research came back with {d.Chars} characters."), "Digested the reply"),
                    DelegateObserved d => MindStep.Of(Wait("still working"), d.Stage == DelegateObserved.Partial ? $"Research is writing ({d.Chars} chars so far)" : "Research is working"),
                    _ => MindStep.Of(Wait("waiting"), "Waiting"),
                };
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("research UK council licensing for scheduling pilots")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .ExpectProposal(Actions.ModelRequest, "pending");
        Assert.Equal(0, external.Calls);   // nothing leaves the machine before approval
        var wait = TimeSpan.FromSeconds(20);
        s.Approve(Actions.ModelRequest)
            .PumpUntil("the delegate to answer", () => s.ForegroundSettled, wait)
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("2000 characters")
            .ExpectEvent(EventTypes.ExternalPackaged)
            .ExpectEvent(EventTypes.ArtifactStored);
        _output.WriteLine(s.Transcript());

        Assert.Equal(1, external.Calls);
        var seen = mind.Requests.SelectMany(r => r.Transcript).OfType<DelegateObserved>().Select(d => d.Stage).Distinct().ToList();
        Assert.Contains(DelegateObserved.Started, seen);
        Assert.Contains(DelegateObserved.Partial, seen);
        Assert.Contains(DelegateObserved.Returned, seen);
        // The partial carried the tail of what had streamed so far; the mind narrated it into the feed.
        Assert.Contains(s.Response.Steps, step => step.StartsWith("Research is writing", StringComparison.Ordinal));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.DecisionMade && r.DataString("decision") == "narrate" && r.DataString("outcome") == "surface");
    }

    [Fact]
    public void TheMindCanStopADelegateItNoLongerNeeds()
    {
        var external = new ChunkedModelClient(chunks: 50, chunkChars: 500, delayMs: 20);
        var mind = new ScriptedMind()
            .Step(Delegate("research", "Write a very long report."), "Asking research", Read(0.8, MindRead.NeedWorldKnowledge))
            .Always(req => req.Transcript[^1] switch
            {
                DelegateObserved { Stage: DelegateObserved.Partial } => MindStep.Of(Stop("That is enough"), "Stopping research"),
                DelegateObserved { Stage: DelegateObserved.Stopped or DelegateObserved.Failed } => MindStep.Of(Say("Stopped the research early."), "Stopped"),
                DelegateObserved { Stage: DelegateObserved.Returned } => MindStep.Of(Say("Research finished before I could stop it."), "Finished"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, cfg => { MindMode(cfg); WithResearchProfile(cfg); }, externalClients: _ => external, inlinePost: false, mind: mind)
            .WithSecret("external-research")
            .Ask("research everything")
            .Approve(Actions.ModelRequest);
        s.PumpUntil("the loop to end", () => s.ForegroundSettled, TimeSpan.FromSeconds(30))
            .ExpectState(RelayState.Ready);
        _output.WriteLine(s.Transcript());
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ExecutionStopRequested && r.DataString("by") == "mind");
        Assert.Contains(mind.Requests.SelectMany(r => r.Transcript).OfType<DelegateObserved>(), d => d.Stage is DelegateObserved.Stopped or DelegateObserved.Failed or DelegateObserved.Returned);
    }

    [Fact]
    public void CancellingATaskEndsItsLoop()
    {
        var mind = new ScriptedMind()
            .Step(Propose(Actions.CreateProject, "You asked for it", ("name", "Harbor")), "Proposing project Harbor", Read(0.2));
        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Ask("create a project called Harbor")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .Cancel()
            .ExpectState(RelayState.Ready)
            .ExpectProject("harbor", exists: false)
            .ExpectEvent(EventTypes.TaskCancelled);
        _output.WriteLine(s.Transcript());
        Assert.Single(mind.Requests);
        Assert.Empty(s.Snap.LiveTasks);
    }

    [Fact]
    public void WaitingApprovalPersistsObjectiveAndWaitingFor()
    {
        var mind = new ScriptedMind()
            .Step(Propose(Actions.CreateProject, "You asked for it", ("name", "Harbor")), "Proposing project Harbor", Read(0.2));
        using var s = Scenario.New(_tmp, MindMode, mind: mind).WithWorkspace()
            .Ask("create a project called Harbor")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/);
        var live = Assert.Single(Directory.GetFiles(s.H.Root.TasksDirectory, "*.live.json"));
        var record = System.Text.Json.JsonSerializer.Deserialize<DurableTaskRecord>(File.ReadAllText(live), Relay.Core.Storage.RelayJson.Indented)!;
        Assert.Contains("Harbor", record.Instruction ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Waits.Approval, record.WaitingFor);
        Assert.True(record.Version >= 0);
    }

    // ----------------------------------------------------------------------------------------
    // Capability building (slice 6): the world clock the mind builds for itself
    // ----------------------------------------------------------------------------------------

    private static string TimeIn(ToolObserved t) => System.Text.Json.JsonDocument.Parse(t.Data!).RootElement.GetProperty("time").GetString()!;

    /// <summary>A mind that builds a world clock when it has none and uses it when it has one; every build stage is narrated.</summary>
    private static ScriptedMind WorldClockMind(string zone) => new ScriptedMind().Always(req => req.Transcript[^1] switch
    {
        InputObserved when req.Context.Tools.Any(t => t.Name == "world_clock") => MindStep.Of(Tool("world_clock", ("zone", zone)), "Asking the world clock", Read(0.2)),
        InputObserved => MindStep.Of(Build("world_clock", "The user asks for the time in other cities and no tool can tell it.", "an IANA zone id", "local time, date and weekday"), "Building a world clock tool", Read(0.6, MindRead.NeedNewTool)),
        PolicyObserved { Outcome: PolicyObserved.NeedsApproval } => MindStep.Of(Wait("approval"), "Waiting for your approval to build"),
        ApprovalObserved { Granted: false } => MindStep.Of(Say("Understood; the tool was not built."), "Not built"),
        BuildObserved { Stage: BuildObserved.Started } => MindStep.Of(Wait("drafting"), "Approved; drafting now"),
        BuildObserved { Stage: BuildObserved.Drafted } => MindStep.Of(Wait("testing"), "Drafted; testing it in the sandbox"),
        BuildObserved { Stage: BuildObserved.Failed } b when b.Detail.Contains("retrying", StringComparison.Ordinal) => MindStep.Of(Wait("retry"), "The first draft failed; trying once more"),
        BuildObserved { Stage: BuildObserved.Promoted } => MindStep.Of(Tool("world_clock", ("zone", zone)), "Asking the new tool"),
        ToolObserved { Tool: "world_clock", Ok: true } t => MindStep.Of(Say($"It is {TimeIn(t)} in {zone}."), "Answered"),
        ToolObserved { Tool: "world_clock" } t => MindStep.Of(Say("The tool failed: " + t.Summary), "The tool failed"),
        BuildObserved { Stage: BuildObserved.Failed or BuildObserved.Unavailable } b => MindStep.Of(Say("I could not build it: " + b.Detail), "Gave up"),
        _ => MindStep.Of(Wait("waiting"), "Waiting"),
    });

    [Fact]
    public void TheMindBuildsAWorldClockToolPromotesItWithOneApprovalAndUsesItToAnswer()
    {
        var drafter = new ScriptedDrafter().Reply(ScriptedDrafter.WorldClock());
        var mind = WorldClockMind("Europe/London");
        var host = new InProcessWorkerHost();
        using var s = Scenario.New(_tmp, MindMode, workerHost: host, inlinePost: false, mind: mind, toolDrafter: drafter).WithWorkspace()
            .Ask("what time is it in London?");
        s.PumpUntil("the build_tool card", () => s.AwaitingUserOrSettled, TimeSpan.FromSeconds(20))
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .ExpectProposal(Actions.BuildTool, "pending")
            .ExpectNoEvent(EventTypes.ToolBuildStarted)
            .ExpectNoEvent(EventTypes.ToolPromoted);
        Assert.Equal(0, drafter.Calls);   // nothing is drafted until the user approves
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ToolsDirectory, "world_clock.json")));
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ToolDraftsDirectory, "world_clock.json")));
        var card = Assert.Single(s.Snap.PendingProposals);
        Assert.Equal("Build the tool 'world_clock'", card.Title);
        Assert.Contains("Nothing is drafted until you approve", card.Detail);
        Assert.Equal("mind:scripted", card.ProposedBy);

        s.Approve(Actions.BuildTool)
            .PumpUntil("the tool to run and the answer", () => s.ForegroundSettled, TimeSpan.FromSeconds(20))
            .ExpectState(RelayState.Ready)
            .ExpectOutcome("executed")
            .ExpectAnswerContains("It is 13:00 in Europe/London")
            .ExpectProposal(Actions.BuildTool, "executed")
            .ExpectEvent(EventTypes.ToolBuildStarted)
            .ExpectEvent(EventTypes.ToolBuildDrafted)
            .ExpectEvent(EventTypes.ToolBuildTested)
            .ExpectEvent(EventTypes.ChangeSetApplied)
            .ExpectEvent(EventTypes.ToolPromoted)
            .ExpectEvent(EventTypes.ToolRan)
            .ExpectEvent(EventTypes.LoopResumed, atLeast: 3);
        _output.WriteLine(s.Transcript());

        // The feed narrates every stage; approval is a card (no separate wait feed — the loop resumes with Started).
        Assert.Equal(new[] { "Building a world clock tool", "Approved; drafting now", "Drafted; testing it in the sandbox", "Asking the new tool", "Answered" }, s.Response.Steps);
        var stages = mind.Requests[^1].Transcript.OfType<BuildObserved>().Select(b => b.Stage).ToList();
        Assert.Equal(new[] { BuildObserved.Started, BuildObserved.Drafted, BuildObserved.Tested, BuildObserved.Promoted }, stages);
        // Approval gates drafting; promotion is covered by that same approval (no second card).
        var kinds = mind.Requests[^1].Transcript.Select(o => o.Kind).ToList();
        Assert.Contains("policy", kinds);
        Assert.Contains("approval", kinds);
        Assert.Equal(kinds.IndexOf("policy"), kinds.FindIndex(k => k == "policy"));
        Assert.True(kinds.IndexOf("policy") < kinds.IndexOf("approval"));
        Assert.True(kinds.IndexOf("approval") < kinds.IndexOf("build"));
        Assert.Contains(mind.Requests[^1].Context.Tools, t => t.Name == "world_clock" && t.Arguments.SequenceEqual(["zone"]));

        // On disk: the promoted package under tools, the draft gone, one change set of kind tool; the promoted tool ran through the sandbox like its test did.
        Assert.True(File.Exists(Path.Combine(_tmp.Root.ToolsDirectory, "world_clock.json")));
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ToolDraftsDirectory, "world_clock.json")));
        var set = Assert.Single(s.H.ChangeSets.All());
        Assert.Equal(Relay.Core.SelfChange.ChangeKinds.Tool, set.Kind);
        Assert.Equal(2, host.Started.Count(r => r.Task == "tool"));                                    // the draft's one test, then the one use
        Assert.All(host.Started, r => Assert.Equal(new[] { "time.zone" }, r.HostAllow));
        var ran = s.H.Last(EventTypes.ToolRan)!;
        Assert.Equal("world_clock", ran.DataString("tool"));
        Assert.Equal(1, ran.DataInt64("hostCalls"));
        Assert.Equal(true, ran.DataBool("ok"));
        var call = Assert.Single(s.Response.ToolCalls);
        Assert.Equal("world_clock", call.Tool);
        Assert.True(call.Ok);

        // The next task has the tool from the start and needs no build; then a revert takes it away again.
        s.Ask("what time is it in London now?")
            .PumpUntil("the second answer", () => s.ForegroundSettled, TimeSpan.FromSeconds(20))
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("It is 13:00 in Europe/London");
        Assert.Equal(1, s.H.Records().Count(r => r.Type == EventTypes.ToolBuildStarted));
        Assert.Equal("Asking the world clock", s.Response.Steps[0]);
        Assert.Equal(3, host.Started.Count(r => r.Task == "tool"));

        var reverted = s.H.ChangeSets.Revert(set.ChangeSetId, "not wanted", s.H.Clock.UtcNow);
        Assert.True(reverted.Ok, reverted.Error);
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ToolsDirectory, "world_clock.json")));
        Assert.False(s.H.Tools!.Store.IsPromoted("world_clock"));
    }

    /// <summary>
    /// The slice 6 acceptance with a real model in both seats: the mind decides that Relay has no clock and builds one, the same model
    /// drafts the package, the sandbox tests it, the user approves the one promotion, and the mind calls the new tool and answers with
    /// the real local time. Runs only with RELAY_LIVE_MODEL_KEY; the transcript goes to RELAY_LIVE_REPORT_DIR.
    /// </summary>
    [Fact]
    public void LiveModelMindBuildsTheWorldClockPromotesItAndAnswersWithIt()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        var host = new InProcessWorkerHost();
        var wait = TimeSpan.FromSeconds(300);
        using var s = Scenario.New(_tmp, cfg => { live.Configure(cfg); MindMode(cfg); cfg.Orchestrator.MaxSteps = 12; }, workerHost: host, inlinePost: false, mind: new ModelMind(client), toolDrafter: client).WithWorkspace();
        try
        {
            s.Ask("What time is it in Tokyo right now?")
             .PumpUntil("the mind to build and the draft to be proposed", () => s.AwaitingUserOrSettled, wait);
            _output.WriteLine(s.Transcript());

            // The mind built rather than guessed: a build_tool card waits before any drafting.
            Assert.Equal(RelayState.Ready /*was AwaitingApproval*/, s.Snap.State);
            var card = Assert.Single(s.Snap.PendingProposals);
            Assert.Equal(Actions.BuildTool, card.Action);
            s.ExpectNoEvent(EventTypes.ToolBuildStarted).ExpectNoEvent(EventTypes.ToolPromoted);
            Assert.Empty(Directory.GetFiles(_tmp.Root.ToolsDirectory));

            s.Approve(Actions.BuildTool)
             .PumpUntil("the tool to run and the answer", () => s.ForegroundSettled, wait)
             .ExpectState(RelayState.Ready)
             .ExpectEvent(EventTypes.ToolBuildStarted)
             .ExpectEvent(EventTypes.ToolBuildTested)
             .ExpectEvent(EventTypes.ToolPromoted)
             .ExpectEvent(EventTypes.ToolRan);
            // T0 is 12:00Z, so Tokyo is 21:00; the mind may say it either way, but it must say the tool's time, not UTC and not a guess.
            var answer = s.Response.Answer ?? "";
            Assert.True(answer.Contains("21:00") || answer.Contains("9:00") || answer.Contains("9 PM", StringComparison.OrdinalIgnoreCase) || answer.Contains("9pm", StringComparison.OrdinalIgnoreCase), "answer: " + answer);
            Assert.Single(Directory.GetFiles(_tmp.Root.ToolsDirectory));
            Assert.Single(s.H.ChangeSets.All());
        }
        finally
        {
            _output.WriteLine(s.Transcript());
            live.Write("mind-build-live.txt", s.Transcript());
            live.Write("mind-build-live.ledger.jsonl", s.H.LedgerText());
            var promoted = Directory.Exists(_tmp.Root.ToolsDirectory) ? Directory.GetFiles(_tmp.Root.ToolsDirectory).Concat(Directory.GetFiles(_tmp.Root.ToolDraftsDirectory, "*.json")).ToList() : [];
            foreach (var file in promoted) live.Write("mind-build-live." + Path.GetFileName(file), File.ReadAllText(file));
        }
    }

    [Fact]
    public void TheMindCanStopABuildItNoLongerNeeds()
    {
        // The first draft is unusable and the retry never answers: after approval, the mind hears the failure, stops the build, and hears the stop.
        var drafter = new ScriptedDrafter().Reply("not a package").Block();
        var mind = new ScriptedMind()
            .Step(Build("world_clock", "why", "", ""), "Building a world clock tool", Read(0.6, MindRead.NeedNewTool))
            .Always(req => req.Transcript[^1] switch
            {
                PolicyObserved { Outcome: PolicyObserved.NeedsApproval } => MindStep.Of(Wait("approval"), "Waiting for approval"),
                BuildObserved { Stage: BuildObserved.Started } => MindStep.Of(Wait("drafting"), "Drafting"),
                BuildObserved { Stage: BuildObserved.Failed } => MindStep.Of(Stop("Never mind, I will answer without it"), "Stopping the build"),
                BuildObserved { Stage: BuildObserved.Stopped } => MindStep.Of(Say("I stopped building the tool; the date today is 2026-09-04."), "Answered without the tool"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, MindMode, workerHost: new InProcessWorkerHost(), inlinePost: false, mind: mind, toolDrafter: drafter)
            .Ask("what time is it in London?");
        s.PumpUntil("the build_tool card", () => s.AwaitingUserOrSettled, TimeSpan.FromSeconds(20))
            .Approve(Actions.BuildTool)
            .PumpUntil("the loop to end", () => s.ForegroundSettled, TimeSpan.FromSeconds(20))
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("stopped building")
            .ExpectEvent(EventTypes.ToolBuildStarted)
            .ExpectEvent(EventTypes.ToolBuildFailed)
            .ExpectEvent(EventTypes.ToolBuildStopped)
            .ExpectNoEvent(EventTypes.ToolBuildDrafted)
            .ExpectNoEvent(EventTypes.ToolPromoted);
        _output.WriteLine(s.Transcript());
        Assert.Equal(2, drafter.Calls);
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ExecutionStopRequested && r.DataString("by") == "mind" && r.DataString("build") == "world_clock");
        Assert.Equal(true, s.H.Last(EventTypes.ToolBuildStopped)!.DataBool("byMind"));
        Assert.Equal(new[] { "Building a world clock tool", "Drafting", "Stopping the build", "Answered without the tool" }, s.Response.Steps);
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ToolsDirectory, "world_clock.json")));
    }

    [Fact]
    public void ARejectedBuildNeverDraftsAndTheMindHearsTheRefusal()
    {
        var drafter = new ScriptedDrafter().Reply(ScriptedDrafter.WorldClock());
        var mind = WorldClockMind("Europe/London");
        using var s = Scenario.New(_tmp, MindMode, workerHost: new InProcessWorkerHost(), inlinePost: false, mind: mind, toolDrafter: drafter)
            .Ask("what time is it in London?");
        s.PumpUntil("the build_tool card", () => s.AwaitingUserOrSettled, TimeSpan.FromSeconds(20))
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .Reject(Actions.BuildTool, "not this one")
            .PumpUntil("the loop to end", () => s.ForegroundSettled, TimeSpan.FromSeconds(20))
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("was not built")
            .ExpectProposal(Actions.BuildTool, "rejected")
            .ExpectNoEvent(EventTypes.ToolBuildStarted)
            .ExpectNoEvent(EventTypes.ToolPromoted)
            .ExpectNoEvent(EventTypes.ChangeSetApplied);
        _output.WriteLine(s.Transcript());
        Assert.Equal(0, drafter.Calls);
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ToolsDirectory, "world_clock.json")));
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ToolDraftsDirectory, "world_clock.json")));
        Assert.DoesNotContain(mind.Requests[^1].Context.Tools, t => t.Name == "world_clock");
    }

    [Fact]
    public void AFailedBuildIsFinalForItsContractAndARepeatedSentenceIsNamedAsAStall()
    {
        // The first live run (docs/09, slice 6): the mind rebuilt the same failed tool and then repeated one sentence with done=false until the
        // step budget ended the task. The loop now refuses the identical rebuild and names the repeated sentence; a changed contract may be built.
        var drafter = new ScriptedDrafter().Reply("not a package").Reply("still not a package").Reply(ScriptedDrafter.WorldClock());
        var mind = new ScriptedMind()
            .Step(Build("time_tokyo", "Need the time in Tokyo", "", "current_time_in_Tokyo"), "Building a tool for Tokyo", Read(0.6, MindRead.NeedNewTool))
            .Always(req => req.Transcript[^1] switch
            {
                PolicyObserved { Outcome: PolicyObserved.NeedsApproval } => MindStep.Of(Wait("approval"), "Waiting for approval"),
                BuildObserved { Stage: BuildObserved.Started } => MindStep.Of(Wait("drafting"), "Drafting"),
                BuildObserved { Stage: BuildObserved.Failed } b when b.Detail.Contains("retrying", StringComparison.Ordinal) => MindStep.Of(Wait("retry"), "Waiting for the retry"),
                BuildObserved { Stage: BuildObserved.Failed } b when b.Detail.Contains("already failed in this task", StringComparison.Ordinal) => MindStep.Of(Say("The tool failed; I will try another way.", done: false), "Saying so"),
                BuildObserved { Stage: BuildObserved.Failed } => MindStep.Of(Build("time_tokyo", "Need the time in Tokyo", "", "current_time_in_Tokyo"), "Trying the same build again"),
                SystemObserved sys when sys.Text.Contains("said exactly that already", StringComparison.Ordinal) => MindStep.Of(Build("world_clock", "A general clock", "an IANA zone id", "local time, date and weekday"), "Building a general clock instead"),
                MoveObserved { Move: SayMove } => MindStep.Of(Say("The tool failed; I will try another way.", done: false), "Saying so again"),
                BuildObserved { Stage: BuildObserved.Drafted } => MindStep.Of(Wait("testing"), "Testing"),
                BuildObserved { Stage: BuildObserved.Promoted } => MindStep.Of(Say("Built a general world clock instead."), "Done"),
                _ => MindStep.Of(Wait("waiting"), "Waiting"),
            });
        using var s = Scenario.New(_tmp, MindMode, workerHost: new InProcessWorkerHost(), inlinePost: false, mind: mind, toolDrafter: drafter)
            .Ask("what time is it in Tokyo?");
        s.PumpUntil("the first build_tool card", () => s.AwaitingUserOrSettled, TimeSpan.FromSeconds(20))
            .Approve(Actions.BuildTool)
            .PumpUntil("the second build to be proposed", () => s.AwaitingUserOrSettled, TimeSpan.FromSeconds(20))
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .ExpectProposal(Actions.BuildTool, "pending");
        _output.WriteLine(s.Transcript());

        var transcript = mind.Requests[^1].Transcript;
        // One real build of time_tokyo (two drafter attempts), the identical rebuild refused without a drafter call, then the general clock proposed.
        Assert.Equal(2, drafter.Calls);
        Assert.Equal(1, s.H.Records().Count(r => r.Type == EventTypes.ToolBuildStarted));
        var refused = Assert.Single(transcript.OfType<BuildObserved>(), b => b.Detail.Contains("already failed in this task", StringComparison.Ordinal));
        Assert.Equal("time_tokyo", refused.Tool);
        Assert.Contains("Do not build it again", refused.Detail);
        var stall = Assert.Single(transcript.OfType<SystemObserved>(), o => o.Text.Contains("said exactly that already", StringComparison.Ordinal));
        Assert.Contains("say it with done=true", stall.Text);
        Assert.Equal(2, transcript.OfType<MoveObserved>().Count(m => m.Move is SayMove));
        Assert.Contains(s.Snap.PendingProposals, p => p.Title == "Build the tool 'world_clock'");
    }

    [Fact]
    public void WithoutASandboxTheBuildMoveIsUnavailableAndTheMindIsToldSo()
    {
        var mind = new ScriptedMind()
            .Step(Build("world_clock", "why", "", ""), "Building a world clock tool", Read(0.6, MindRead.NeedNewTool))
            .Then(req =>
            {
                var build = Assert.IsType<BuildObserved>(req.Transcript[^1]);
                Assert.Equal(BuildObserved.Unavailable, build.Stage);
                return MindStep.Of(Say("I would need a world clock tool, which cannot be built here."), "Explained");
            });
        using var s = Scenario.New(_tmp, MindMode, mind: mind)
            .Ask("what time is it in London?")
            .ExpectState(RelayState.Ready)
            .ExpectAnswerContains("cannot be built here")
            .ExpectNoEvent(EventTypes.ToolBuildStarted);
        Assert.False(mind.Requests[0].Context.CanBuild);
    }

    /// <summary>
    /// The mode decides whether anything is interpreted, not whether a mind happens to be in place. Everything
    /// above runs under <c>mind</c>; under <c>off</c> the same mind is never asked, so an instruction is recorded
    /// and nothing is planned, proposed or run.
    /// </summary>
    [Fact]
    public void WithTheModeOffTheMindIsNeverConsultedAndTheInstructionIsOnlyRecorded()
    {
        var mind = new ScriptedMind().Always(_ => throw new InvalidOperationException("the mind must not be consulted with the mode off"));
        using var s = Scenario.New(_tmp, cfg => cfg.Orchestrator.Mode = OrchestratorSettings.Off, mind: mind).WithWorkspace()
            .Command("create project Harbor")
            .ExpectState(RelayState.Ready)
            .ExpectProject("harbor", exists: false)
            .ExpectTaskCount(0)
            .ExpectNoEvent(EventTypes.MindStepped);
        Assert.Empty(mind.Requests);
        Assert.Equal(false, s.H.Last(EventTypes.CommandRecorded)!.DataBool("executed"));
    }

    public void Dispose() => _tmp.Dispose();
}

/// <summary>An external model whose reply streams in fixed-size chunks, so partial observation has something to observe.</summary>
public sealed class ChunkedModelClient : IModelClient
{
    private readonly int _chunks;
    private readonly int _chunkChars;
    private readonly int _delayMs;

    public ChunkedModelClient(int chunks, int chunkChars, int delayMs = 0) { _chunks = chunks; _chunkChars = chunkChars; _delayMs = delayMs; }

    public string Host => "api.example.test";
    public string Model => "gpt-5-nano";
    public int Calls { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        => StreamAsync(request, (_, _) => Task.CompletedTask, cancellationToken);

    public async Task<ModelResponse> StreamAsync(ModelRequest request, Func<string, CancellationToken, Task> onDelta, CancellationToken cancellationToken)
    {
        Calls++;
        var all = new System.Text.StringBuilder();
        for (var i = 0; i < _chunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_delayMs > 0) await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            var chunk = new string((char)('a' + i % 26), _chunkChars - 1) + " ";
            all.Append(chunk);
            await onDelta(chunk, cancellationToken).ConfigureAwait(false);
        }
        return new ModelResponse(true, all.ToString(), 200, _chunks * 100, 300, null);
    }
}
