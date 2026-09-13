using Relay.Core.Agents;
using Relay.Core.Config;
using Relay.Core.Decisions;
using Relay.Core.Execution;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Policy;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>
/// Step 2: the ready queue and local-inference gate, durable task fields, hard cancel of late completions,
/// and resume of waiting work after a simulated restart.
/// </summary>
public class TaskEngineTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private static void Mind(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    [Fact]
    public async Task TwoReadyTasksShareOneInferenceSlot()
    {
        using var engine = new TaskEngine(_ => { });
        var gate = new ConcurrentGateMind();
        var host = new ImmediateHost();
        var decider = new Decider(DecisionSet.Default());
        var context = new MindContext();

        TaskLoop Loop(string id) => new(id, InputObserved.Ask, gate, host, context, decider)
        {
            AcquireInference = engine.AcquireInferenceAsync,
            ReleaseInference = engine.ReleaseInference,
        };

        var a = Loop("a");
        var b = Loop("b");
        a.Observe(new InputObserved(Harness.T0, InputObserved.Ask, "one"));
        b.Observe(new InputObserved(Harness.T0, InputObserved.Ask, "two"));

        var runA = a.RunAsync(CancellationToken.None);
        var runB = b.RunAsync(CancellationToken.None);
        Assert.True(gate.FirstEntered.Wait(TimeSpan.FromSeconds(2)), "first StepAsync should hold the gate");
        await Task.Delay(100);
        Assert.Equal(1, gate.TotalSteps);          // the other task is blocked on AcquireInference, not inside StepAsync
        Assert.Equal(1, gate.MaxConcurrent);
        gate.ReleaseAll();
        Assert.True(gate.SecondEntered.Wait(TimeSpan.FromSeconds(2)), "second StepAsync runs after the first releases");
        gate.ReleaseAll();
        await Task.WhenAll(runA, runB);
        Assert.Equal(2, gate.TotalSteps);
        Assert.Equal(1, gate.MaxConcurrent);
    }

    [Fact]
    public void LateCompletePendingOperationAfterCancelIsNoOp()
    {
        var mind = new ScriptedMind().Always(request => request.Transcript[^1] is InputObserved
            ? MindStep.Of(Propose(Actions.CreateProject, "You asked for it", ("name", "Atlas")), "Proposing", Read(0.2))
            : MindStep.Of(Say("done"), "Done"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .Cancel()
            .ExpectEvent(EventTypes.TaskCancelled);

        var proposalId = s.H.Last(EventTypes.ProposalReceived)!.DataString("proposalId")!;
        s.H.Coordinator.CompletePendingOperation(proposalId, ExecutionResult.Ok("late arrival"));
        Assert.Equal(0, s.H.Count(EventTypes.ExecutionStarted));
        Assert.Equal(0, s.H.Count(EventTypes.ExecutionCompleted));
        Assert.DoesNotContain(s.H.Records(), r => r.Type == EventTypes.TaskCompleted);
        Assert.Equal(TaskStatus.Cancelled, s.Snap.Response?.Status);
    }

    [Fact]
    public void LateCompletePendingOperationAfterShutdownWhileExecutingIsNoOp()
    {
        var host = new InProcessWorkerHost { Body = WorkerBodies.Hanging };
        var mind = new ScriptedMind();
        using var s = Scenario.New(_tmp, configure: Mind, workerHost: host, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note("We decided the Atlas beta ships on October 14.");
        mind.Always(request => request.Transcript[^1] switch
        {
            ExecutionObserved => MindStep.Of(Wait("the worker is running"), "Waiting"),
            _ => MindStep.Of(Propose(Actions.LaunchWorker, "Summarize Atlas.",
                    ("projectId", "atlas"), ("task", "summarize"), ("objective", "Summarize the Atlas notes")),
                "Proposing worker", Read(0.5, MindRead.NeedLocalNotes)),
        });

        s.Command("summarize atlas").Approve(Actions.LaunchWorker).Advance(TimeSpan.Zero);
        var proposalId = s.Snap.Response!.Proposals.First(p => p.Action == Actions.LaunchWorker).ProposalId;

        s.H.Coordinator.Shutdown("test");
        var beforeCompleted = s.H.Count(EventTypes.ExecutionCompleted);
        var beforeTaskCompleted = s.H.Count(EventTypes.TaskCompleted);
        s.H.Coordinator.CompletePendingOperation(proposalId, ExecutionResult.Ok("late after cancel"));
        Assert.Equal(beforeCompleted, s.H.Count(EventTypes.ExecutionCompleted));
        Assert.Equal(beforeTaskCompleted, s.H.Count(EventTypes.TaskCompleted));
    }

    [Fact]
    public void DurableLiveJsonFieldsArePresentWhileWaiting()
    {
        var mind = new ScriptedMind().Always(request => request.Transcript[^1] is InputObserved
            ? MindStep.Of(Propose(Actions.CreateProject, "You asked for it", ("name", "Market")), "Proposing", Read(0.2))
            : MindStep.Of(Say("ready"), "Done"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("Create Market Study")
            .ExpectState(RelayState.Ready);
        var path = Assert.Single(Directory.GetFiles(s.H.Root.TasksDirectory, "*.live.json"));
        var record = System.Text.Json.JsonSerializer.Deserialize<DurableTaskRecord>(File.ReadAllText(path), Relay.Core.Storage.RelayJson.Indented)!;
        Assert.False(string.IsNullOrWhiteSpace(record.Instruction));
        Assert.Equal(record.Instruction, record.Objective);
        Assert.Equal(Waits.Approval, record.WaitingFor);
        Assert.NotNull(record.PlanSummary);
        Assert.True(record.Version >= 0);
        Assert.Equal("awaiting_approval", record.Stage);
        Assert.True(record.InstructionChars > 0);
        Assert.NotNull(record.Proposals);
        Assert.Contains(record.Proposals!, p => p.Action == Actions.CreateProject);
    }

    [Fact]
    public void WaitingTaskResumesAfterSimulatedRestart()
    {
        var mind = new ScriptedMind().Always(request => request.Transcript[^1] is InputObserved
            ? MindStep.Of(Propose(Actions.CreateProject, "You asked for it", ("name", "ResumeMe")), "Proposing", Read(0.2))
            : MindStep.Of(Say("ResumeMe is ready."), "Done"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create ResumeMe")
            .ExpectState(RelayState.Ready /*was AwaitingApproval*/)
            .CrashAndRestart()
            .ExpectState(RelayState.Ready);
        Assert.Equal(true, s.H.Last(EventTypes.TaskInterruptedFound)!.DataBool("resumed"));
        Assert.Contains(s.Snap.LiveTasks, t => t.Status == TaskStatus.AwaitingApproval);
        s.Approve(Actions.CreateProject)
            .ExpectOutcome("executed")
            .ExpectProject("resumeme");
    }

    /// <summary>Holds every StepAsync until ReleaseAll; tracks concurrent entries into the mind.</summary>
    private sealed class ConcurrentGateMind : IMind
    {
        private int _concurrent;
        private readonly List<TaskCompletionSource> _gates = new();
        private readonly object _sync = new();
        public string Name => "gate";
        public int MaxConcurrent { get; private set; }
        public int TotalSteps { get; private set; }
        public ManualResetEventSlim FirstEntered { get; } = new(false);
        public ManualResetEventSlim SecondEntered { get; } = new(false);

        public Task<MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _concurrent);
            int total;
            lock (_sync)
            {
                if (now > MaxConcurrent) MaxConcurrent = now;
                TotalSteps++;
                total = TotalSteps;
            }
            if (total == 1) FirstEntered.Set();
            if (total == 2) SecondEntered.Set();
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _gates.Add(tcs);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return Finish(tcs);
        }

        private async Task<MindStep> Finish(TaskCompletionSource tcs)
        {
            try
            {
                await tcs.Task.ConfigureAwait(false);
                return MindStep.Of(Say("done", done: true), "Done");
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }

        public void ReleaseAll()
        {
            List<TaskCompletionSource> gates;
            lock (_sync) { gates = _gates.ToList(); _gates.Clear(); }
            foreach (var g in gates) g.TrySetResult();
        }
    }

    private sealed class ImmediateHost : ILoopHost
    {
        public void Stepped(TaskLoop loop, MindStep step) { }
        public void Said(TaskLoop loop, SayMove move) { }
        public Task<MoveOutcome> UseToolAsync(TaskLoop loop, UseToolMove move, CancellationToken cancellationToken) => Task.FromResult(MoveOutcome.Nothing);
        public Task<MoveOutcome> ProposeAsync(TaskLoop loop, ProposeMove move, DecisionRecord? fof, CancellationToken cancellationToken) => Task.FromResult(MoveOutcome.Nothing);
        public Task<MoveOutcome> DelegateAsync(TaskLoop loop, DelegateMove move, CancellationToken cancellationToken) => Task.FromResult(MoveOutcome.Nothing);
        public Task<MoveOutcome> BuildAsync(TaskLoop loop, BuildMove move, DecisionRecord fof, CancellationToken cancellationToken) => Task.FromResult(MoveOutcome.Nothing);
        public Task<MoveOutcome> RunWorkflowAsync(TaskLoop loop, RunWorkflowMove move, CancellationToken cancellationToken) => Task.FromResult(MoveOutcome.Nothing);
        public Task<MoveOutcome> AskUserAsync(TaskLoop loop, AskUserMove move, CancellationToken cancellationToken) => Task.FromResult(MoveOutcome.Nothing);
        public Task<MoveOutcome> StopAsync(TaskLoop loop, StopMove move, string waitingFor, CancellationToken cancellationToken) => Task.FromResult(MoveOutcome.Nothing);
        public void Waiting(TaskLoop loop, string waitingFor) { }
        public void Ended(TaskLoop loop, LoopResult result) { }
    }
}
