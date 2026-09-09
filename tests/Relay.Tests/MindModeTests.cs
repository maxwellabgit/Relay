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
            .ExpectState(RelayState.Completed)
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
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.CreateProject, "pending")
            .ExpectEvent(EventTypes.LoopWaiting)
            .ExpectProject("harbor", exists: false);
        Assert.Single(mind.Requests); // nothing was stepped while the user decided
        s.Approve(Actions.CreateProject)
            .ExpectState(RelayState.Completed)
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
            .ExpectState(RelayState.AwaitingApproval)
            .Reject(Actions.CreateProject, "not now")
            .ExpectState(RelayState.Completed)
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
            .ExpectState(RelayState.AwaitingApproval)   // waiting for the user's words, shown as the interim answer
            .ExpectAnswerContains("What should the project be called?")
            .ExpectProposal(Actions.CreateProject, "denied");
        Assert.Empty(s.Snap.PendingProposals);
        // The typed reply goes to the waiting task, not to a new one.
        s.Ask("Harbor").ExpectState(RelayState.Completed).ExpectAnswerContains("Got it: Harbor").ExpectTaskCount(1);
        _output.WriteLine(s.Transcript());
        Assert.Equal(3, mind.Requests.Count);
    }

    [Fact]
    public void ContractFailuresAreRetriedThenTheTaskFailsVisibly()
    {
        var mind = new ScriptedMind().Raw("not json").Raw("{\"move\":{\"type\":\"say\"}}").Raw("still not json");
        using var s = Scenario.New(_tmp, MindMode, mind: mind)
            .Ask("hello")
            .ExpectState(RelayState.Failed)
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
            .ExpectState(RelayState.Failed)
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
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.ModelRequest, "pending");
        Assert.Equal(0, external.Calls);   // nothing leaves the machine before approval
        var wait = TimeSpan.FromSeconds(20);
        s.Approve(Actions.ModelRequest)
            .PumpUntil("the delegate to answer", () => s.Snap.State is RelayState.Completed or RelayState.Failed, wait)
            .ExpectState(RelayState.Completed)
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
        s.PumpUntil("the loop to end", () => s.Snap.State is RelayState.Completed or RelayState.Failed, TimeSpan.FromSeconds(30))
            .ExpectState(RelayState.Completed);
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
            .ExpectState(RelayState.AwaitingApproval)
            .Cancel()
            .ExpectState(RelayState.Idle)
            .ExpectProject("harbor", exists: false)
            .ExpectEvent(EventTypes.TaskCancelled);
        _output.WriteLine(s.Transcript());
        Assert.Single(mind.Requests);
        Assert.Empty(s.Snap.LiveTasks);
    }

    [Fact]
    public void RulesModeIsUntouchedWhenTheMindIsConfiguredButNotSelected()
    {
        var mind = new ScriptedMind().Always(_ => throw new InvalidOperationException("the mind must not be consulted in rules mode"));
        using var s = Scenario.New(_tmp, cfg => cfg.Orchestrator.Mode = OrchestratorSettings.Rules, mind: mind).WithWorkspace()
            .Command("create project Harbor").Approve().ExpectProject("harbor").ExpectState(RelayState.Completed)
            .ExpectNoEvent(EventTypes.MindStepped);
        Assert.Empty(mind.Requests);
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
