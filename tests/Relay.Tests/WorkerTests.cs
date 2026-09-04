using Relay.Core.Agents;
using Relay.Core.Ledger;
using Relay.Core.Policy;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Tests.Support;

namespace Relay.Tests;

/// <summary>Phase 5: sandboxed worker runs through the broker, limits, hostile workers, stop, and the separate apply approval.</summary>
public class WorkerTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private static Scenario ProjectWithNotes(TempRoot tmp, InProcessWorkerHost host, Action<Relay.Core.Config.RelaySettings>? configure = null)
        => Scenario.New(tmp, configure: configure, workerHost: host).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Note("We decided the Atlas beta ships on October 14.")
            .Note("Need to email the Atlas pilot customers before the beta.")
            .ExpectEvent(EventTypes.NoteRouted, atLeast: 2);

    [Fact]
    public void SummaryIsProducedInStagingByAWorkerAndAppliedOnlyAfterASecondApproval()
    {
        var host = new InProcessWorkerHost();
        using var s = ProjectWithNotes(_tmp, host)
            .Command("summarize atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.LaunchWorker, "pending")
            .Approve(Actions.LaunchWorker)
            .ExpectState(RelayState.Executing)              // the worker is a pending operation: the turn stays open and visible
            .ExpectEvent(EventTypes.AgentRunLaunched)
            .ExpectProposal(Actions.LaunchWorker, "executing")
            .Advance(TimeSpan.Zero)                          // the deferred start runs the whole worker inline
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("executed")
            .ExpectProposal(Actions.LaunchWorker, "executed")
            .ExpectEvent(EventTypes.AgentRunLog)
            .ExpectEvent(EventTypes.AgentRunToolCalled, atLeast: 4) // list_dir, 2× read_file, write_file
            .ExpectEvent(EventTypes.AgentRunCompleted)
            .ExpectNoEvent(EventTypes.AgentRunToolDenied)
            .ExpectNoEvent(EventTypes.PatchApplied);

        var spec = Assert.Single(host.Started);
        Assert.Equal(2, spec.Inputs.Count);
        Assert.False(spec.Network);
        var specMessage = spec.ToSpecMessage();
        Assert.DoesNotContain(":\\", specMessage);                 // the worker is told no absolute path, not even its own
        Assert.DoesNotContain("ledger", specMessage, StringComparison.OrdinalIgnoreCase);
        Assert.All(spec.Inputs, i => Assert.True((File.GetAttributes(Path.Combine(spec.StagingPath, i.Path)) & FileAttributes.ReadOnly) != 0));

        var summaryPath = Path.Combine(spec.OutPath, "summary.md");
        var summary = File.ReadAllText(summaryPath);
        Assert.Contains("## Decisions (1)", summary);
        Assert.Contains("October 14", summary);
        Assert.Contains("## Tasks (1)", summary);
        Assert.Contains("inputs/decisions/", summary);
        var status = AgentRunStatus.Load(spec.StagingPath)!;
        Assert.Equal("completed", status.State);
        Assert.False(status.Applied);

        var project = s.H.Registry.FindActive("atlas")!;
        var artifact = Path.Combine(project.RootPath, "artifacts", "summary.md");
        Assert.False(File.Exists(artifact)); // derived content never lands in the project without its own approval

        s.Command("apply the summary to atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.ApplyPatch, "pending")
            .Approve(Actions.ApplyPatch)
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("executed")
            .ExpectEvent(EventTypes.PatchApplied);
        Assert.True(File.Exists(artifact));
        Assert.Equal(summary, File.ReadAllText(artifact));
        Assert.True(AgentRunStatus.Load(spec.StagingPath)!.Applied);

        s.Command("apply the summary to atlas")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("No completed worker output is waiting")
            .ExpectNoProposals();

        // Applying again over an existing artifact versions the previous file instead of overwriting it.
        s.Command("summarize atlas").Approve(Actions.LaunchWorker).Advance(TimeSpan.Zero).ExpectState(RelayState.Completed)
         .Command("apply the summary to atlas").Approve(Actions.ApplyPatch).ExpectState(RelayState.Completed);
        var versions = Path.Combine(project.RootPath, ".orchestrator", "versions", "artifacts");
        Assert.Single(Directory.EnumerateFiles(versions));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.PatchApplied && r.DataString("previousVersionPath") is not null);
    }

    [Fact]
    public void HostileWorkerIsDeniedEverywhereAndATamperedRunIsRejected()
    {
        var host = new InProcessWorkerHost { Body = WorkerBodies.Hostile };
        using var s = ProjectWithNotes(_tmp, host)
            .Command("summarize atlas").Approve(Actions.LaunchWorker)
            .Advance(TimeSpan.Zero)
            .ExpectState(RelayState.Failed)
            .ExpectEvent(EventTypes.AgentRunToolDenied, atLeast: 9)
            .ExpectEvent(EventTypes.AgentRunTerminated)
            .ExpectNoEvent(EventTypes.PatchApplied);

        var denied = s.H.Records().Where(r => r.Type == EventTypes.AgentRunToolDenied).Select(r => r.DataString("reason")!).ToList();
        Assert.Contains(denied, r => r.Contains("not in this run's allowlist"));        // run_shell
        Assert.Contains(denied, r => r.Contains("outside the write allowlist"));         // inputs/notes/planted.md
        Assert.Contains(denied, r => r.Contains("outside the read allowlist"));          // list_dir out
        Assert.Contains(denied, r => r.Contains("not a plain relative path"));           // traversal and absolute paths
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.AgentRunLog && r.DataString("text")!.StartsWith("9 of 9"));

        var terminated = s.H.Last(EventTypes.AgentRunTerminated)!;
        Assert.Contains("inputs were modified", terminated.DataString("reason"));
        Assert.Equal("execution_failed", s.Snap.Incident?.Kind);

        var spec = Assert.Single(host.Started);
        Assert.False(File.Exists(Path.Combine(spec.StagingPath, "inputs", "notes", "planted.md")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(spec.StagingPath)!, "escaped.txt")));
        Assert.Equal("terminated", AgentRunStatus.Load(spec.StagingPath)!.State);

        s.Dismiss().ExpectState(RelayState.Idle)
         .Command("apply the summary to atlas")
         .ExpectAnswerContains("No completed worker output is waiting"); // a terminated run's output can never be applied
    }

    [Fact]
    public void HangingWorkerIsKilledAtTheWallClockLimit()
    {
        var host = new InProcessWorkerHost { Body = WorkerBodies.Hanging };
        using var s = ProjectWithNotes(_tmp, host, configure: x => x.Workers.WallClockSeconds = 5)
            .Command("summarize atlas").Approve(Actions.LaunchWorker)
            .Advance(TimeSpan.Zero)
            .ExpectState(RelayState.Executing)
            .Advance(TimeSpan.FromSeconds(4))
            .ExpectState(RelayState.Executing)
            .Advance(TimeSpan.FromSeconds(2))
            .ExpectState(RelayState.Failed)
            .ExpectEvent(EventTypes.AgentRunTerminated);
        Assert.Contains("wall clock limit of 5s", s.H.Last(EventTypes.AgentRunTerminated)!.DataString("reason"));
        Assert.Equal(0, s.H.Workers!.ActiveRuns);
    }

    [Fact]
    public void CancelDuringAWorkerRunStopsItWithoutRaisingAnIncident()
    {
        var host = new InProcessWorkerHost { Body = WorkerBodies.Hanging };
        using var s = ProjectWithNotes(_tmp, host)
            .Command("summarize atlas").Approve(Actions.LaunchWorker)
            .Advance(TimeSpan.Zero)
            .ExpectState(RelayState.Executing)
            .Cancel()
            .ExpectEvent(EventTypes.ExecutionStopRequested)
            .ExpectEvent(EventTypes.AgentRunTerminated)
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("stopped");
        Assert.Null(s.Snap.Incident);
        Assert.Contains("Stopped", s.Snap.Receipt);
        Assert.Contains("stop requested", s.H.Last(EventTypes.AgentRunTerminated)!.DataString("reason"));
    }

    [Fact]
    public void ShutdownDuringAWorkerRunTerminatesTheWorkerAndRecoveryReportsIt()
    {
        var host = new InProcessWorkerHost { Body = WorkerBodies.Hanging };
        using var s = ProjectWithNotes(_tmp, host)
            .Command("summarize atlas").Approve(Actions.LaunchWorker)
            .Advance(TimeSpan.Zero)
            .ExpectState(RelayState.Executing)
            .Restart();
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.AgentRunTerminated);
        Assert.Equal(0, s.H.Workers!.ActiveRuns);
        s.ExpectState(RelayState.Idle);
    }

    [Fact]
    public void WorkersCanBeDisabledInSettingsAndPolicyDeniesTheLaunch()
    {
        using var s = Scenario.New(_tmp, configure: x => x.Workers.Enabled = false, workerHost: new InProcessWorkerHost()).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("summarize atlas")
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("denied")
            .ExpectProposal(Actions.LaunchWorker, "denied")
            .ExpectNoEvent(EventTypes.AgentRunLaunched);
    }

    [Fact]
    public void RealWorkerProcessCompletesASummaryOverThePipe()
    {
        var workerDll = Path.Combine(AppContext.BaseDirectory, "Relay.Worker.dll");
        Assert.True(File.Exists(workerDll), "Relay.Worker.dll should be built beside the tests.");
        // The real Windows sandbox: the child runs inside a job object with kill-on-close, a memory cap and UI restrictions.
        var host = new Relay.Windows.JobObjectWorkerHost(workerDll);

        using var h = new Harness(_tmp.Root, workerHost: host, inlinePost: false).Start();
        var ws = Path.Combine(Path.GetDirectoryName(_tmp.Root.Path)!, Path.GetFileName(_tmp.Root.Path) + "-ws");
        Directory.CreateDirectory(ws);
        try
        {
            Assert.True(h.Coordinator.RegisterWorkspace(ws));
            Assert.True(h.Coordinator.CreateProject("Atlas"));
            var project = h.Registry.FindActive("atlas")!;
            var note = new Relay.Core.Notes.NoteDocument { Id = "N1", ProjectId = project.Id, Type = "decision", Created = Harness.T0, Body = "We decided the Atlas beta ships on October 14." };
            Relay.Core.Notes.ProjectNoteStore.WriteNew(project.RootPath, note);

            h.Coordinator.PressCommandKey();
            h.Coordinator.TextChanged("summarize atlas");
            h.Coordinator.PressCommandKey();
            h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            Assert.True(h.Scheduler.PumpUntil(() => h.Snap.State == RelayState.AwaitingApproval, TimeSpan.FromSeconds(10)), "planning did not finish; state " + h.Snap.State);
            h.Coordinator.ApproveAll();
            Assert.Equal(RelayState.Executing, h.Snap.State);
            h.Scheduler.Advance(TimeSpan.Zero); // starts the real process

            var finished = h.Scheduler.PumpUntil(() => h.Snap.State is RelayState.Completed or RelayState.Failed, TimeSpan.FromSeconds(90));
            Assert.True(finished, "worker did not finish in time; state " + h.Snap.State);
            Assert.Equal(RelayState.Completed, h.Snap.State);
            Assert.Equal("executed", h.Snap.Response!.Outcome);

            var launched = h.Last(EventTypes.AgentRunLaunched)!;
                Assert.Contains("job object sandbox", launched.DataString("host"));
            var completed = h.Last(EventTypes.AgentRunCompleted)!;
            Assert.True(completed.DataBool("ok"));
            Assert.Equal(0, completed.DataInt64("exitCode"));
            Assert.True(h.Count(EventTypes.AgentRunToolCalled) >= 3);
            var staging = Path.Combine(_tmp.Root.AgentsDirectory, launched.DataString("runId")!);
            Assert.Contains("October 14", File.ReadAllText(Path.Combine(staging, "out", "summary.md")));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BrokerConfinesPathsAndBudgets()
    {
        var staging = Path.Combine(_tmp.Root.AgentsDirectory, "run1");
        Directory.CreateDirectory(Path.Combine(staging, "inputs"));
        Directory.CreateDirectory(Path.Combine(staging, "out"));
        File.WriteAllText(Path.Combine(staging, "inputs", "a.md"), "hello");
        var spec = new AgentRunSpec
        {
            RunId = "run1", ProposalId = "p", TurnId = "t", ProjectId = "P", ProjectSlug = "p", Task = "summarize", Objective = "o",
            Inputs = [], Limits = new AgentLimits(60, 1024, 16, 3, 64 * 1024 * 1024), StagingPath = staging, RequiredOutputs = ["out/summary.md"], CreatedAt = Harness.T0,
        };
        var broker = new WorkerBroker(spec);

        static string Call(int id, string tool, string path, string? text = null)
            => $"{{\"type\":\"call\",\"id\":{id},\"tool\":\"{tool}\",\"args\":{{\"path\":\"{path.Replace("\\", "\\\\")}\"{(text is null ? "" : $",\"text\":\"{text}\"")}}}}}";

        Assert.Contains("\"ok\":true", broker.Handle(Call(1, "read_file", "inputs/a.md")).Reply);
        Assert.Contains("write budget", broker.Handle(Call(2, "write_file", "out/summary.md", "this text is longer than sixteen bytes")).Reply);
        Assert.Contains("\"ok\":true", broker.Handle(Call(3, "write_file", "out/summary.md", "short")).Reply);
        Assert.Contains("tool call limit", broker.Handle(Call(4, "read_file", "inputs/a.md")).Reply);
        Assert.Equal(3, broker.ToolCalls);
        Assert.Equal(2, broker.Denied);

        Assert.False(broker.Handle("not json").Finished);
        Assert.True(broker.Handle("{\"type\":\"done\",\"summary\":\"ok\",\"outputs\":[\"out/summary.md\"]}").Finished);
        Assert.True(broker.Done);
        Assert.Equal("ok", broker.Summary);
    }

    public void Dispose() => _tmp.Dispose();
}
