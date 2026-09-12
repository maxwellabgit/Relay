using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>
/// The life of a task, from the chord that starts it to the record it leaves: the order the ledger
/// tells it in, policy's verdict, the user's approval or refusal or edit, the executor, and every way
/// a task can end that is not success — cancelled, timed out, thrown, interrupted by a crash. What the
/// mind decides is scripted here, because none of this is about what it decided: it is about the
/// deterministic engine around it, which owns every consequence and is the same whatever the mind says.
/// </summary>
public class TaskLifecycleTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public TaskLifecycleTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>The mind runs every task; without one in place nothing interprets anything and no task can run.</summary>
    private static void Mind(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    /// <summary>
    /// A mind that asks for one operation and then says it is done: the proposal on the first step, and on
    /// every step after it the sentence that ends the task. Two steps are the minimum, because the loop stops
    /// between them to let policy and the user decide — which is the part these tests watch.
    /// </summary>
    private static ScriptedMind Proposing(string action, string done, params (string Key, string Value)[] target)
        => new ScriptedMind().Always(request => request.Transcript[^1] is InputObserved
            ? MindStep.Of(Propose(action, "You asked for it", target), $"Proposing {action}", Read(0.2))
            : MindStep.Of(Say(done), "Done"));

    [Fact]
    public void CreateProjectNeedsApprovalThenExecutesThroughTheFullPath()
    {
        var mind = Proposing(Actions.CreateProject, "Market Study is ready.", ("name", "Market Study"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("Create a project called Market Study")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.CreateProject, "pending")
            .ExpectProject("market-study", exists: false);
        // The pending proposal is shown once, in Response next to the feed; Review is for everything else.
        Assert.Single(s.Snap.PendingProposals);
        Assert.Empty(s.Snap.Review);
        s.Approve(Actions.CreateProject)
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("executed")
            .ExpectProposal(Actions.CreateProject, "executed")
            .ExpectProject("market-study");
        _output.WriteLine(s.Transcript()); // the reviewable artifact: how Relay handled the instruction, step by step

        var types = s.H.Records().Select(r => r.Type).ToList();
        var expectedOrder = new[]
        {
            EventTypes.CaptureCommitted, EventTypes.CommandRecorded, EventTypes.TaskCreated, EventTypes.TurnStarted, EventTypes.MindStepped, EventTypes.ProposalReceived,
            EventTypes.ProposalDecided, EventTypes.ApprovalGranted, EventTypes.ExecutionStarted, EventTypes.ProjectCreated, EventTypes.ExecutionCompleted, EventTypes.LoopEnded, EventTypes.TaskCompleted,
        };
        var last = -1;
        foreach (var type in expectedOrder)
        {
            var i = types.IndexOf(type, last + 1);
            Assert.True(i > last, $"{type} missing or out of order.\n{s.Transcript()}");
            last = i;
        }
        var project = s.H.Registry.FindActive("market-study")!;
        Assert.Empty(ProjectLayout.Verify(project.RootPath));
        Assert.StartsWith(s.WorkspacePath, project.RootPath, StringComparison.OrdinalIgnoreCase);
        var approval = s.H.Last(EventTypes.ApprovalGranted)!;
        Assert.Equal(s.H.Last(EventTypes.ProposalReceived)!.DataString("hash"), approval.DataString("proposalHash"));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ProposalReceived && r.DataString("proposedBy") == mind.Name);
    }

    [Fact]
    public void WithoutAProjectFolderCreateProjectIsDeniedVisibly()
    {
        var mind = Proposing(Actions.CreateProject, "There is nowhere to put it yet.", ("name", "Atlas"));
        using var s = Scenario.New(_tmp, Mind, mind: mind)
            .Command("create a project called Atlas")
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("denied")
            .ExpectProposal(Actions.CreateProject, "denied")
            .ExpectNoEvent(EventTypes.ExecutionStarted)
            .ExpectNoEvent(EventTypes.ApprovalGranted);
        var denied = s.Response.Proposals.Single();
        Assert.Contains(denied.Reasons, r => r.Contains("New project", StringComparison.Ordinal)); // points at the one way to pick a folder
        Assert.Equal("Deny", s.H.Last(EventTypes.ProposalDecided)!.DataString("outcome"));
    }

    [Fact]
    public void NewProjectInAFolderRegistersItOnceAndLaterProjectsReuseIt()
    {
        var folder = Path.Combine(Path.GetDirectoryName(_tmp.Root.Path)!, Path.GetFileName(_tmp.Root.Path) + "-projects");
        Directory.CreateDirectory(folder);
        try
        {
            var mind = Proposing(Actions.CreateProject, "Harbor is ready.", ("name", "Harbor"));
            using var s = Scenario.New(_tmp, Mind, mind: mind);
            Assert.Empty(s.Snap.Workspaces);

            // The dialog path: the chosen folder becomes a registered project folder, then the project is created in it.
            s.Do("New project… in a fresh folder", c => Assert.True(c.CreateProjectIn(folder, "Atlas")))
                .ExpectState(RelayState.Completed)
                .ExpectEvent(EventTypes.WorkspaceRegistered)
                .ExpectEvent(EventTypes.ProjectCreated)
                .ExpectProject("atlas");
            Assert.Single(s.Snap.Workspaces);
            Assert.StartsWith(folder, s.H.Registry.FindActive("atlas")!.RootPath, StringComparison.OrdinalIgnoreCase);

            // A second project in the same folder does not register it again.
            s.Do("New project… in the same folder", c => Assert.True(c.CreateProjectIn(folder, "Garden")))
                .ExpectProject("garden");
            Assert.Equal(1, s.H.Count(EventTypes.WorkspaceRegistered));

            // Asked for by voice, with no folder named, the registered folder is the default.
            s.Command("create a project called Harbor").Approve()
                .ExpectProject("harbor");
            Assert.StartsWith(folder, s.H.Registry.FindActive("harbor")!.RootPath, StringComparison.OrdinalIgnoreCase);

            // An empty folder is refused before anything is registered or proposed.
            s.Do("New project… without a folder", c => Assert.False(c.CreateProjectIn("", "Nowhere")));
            Assert.Contains("Choose the folder", s.Snap.Notice);
            Assert.Equal(1, s.H.Count(EventTypes.WorkspaceRegistered));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RejectingRunsNothing()
    {
        var mind = Proposing(Actions.CreateProject, "Understood, nothing was created.", ("name", "Atlas"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create a project called Atlas")
            .Reject(Actions.CreateProject, "not now")
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("rejected")
            .ExpectProject("atlas", exists: false)
            .ExpectEvent(EventTypes.ApprovalRejected)
            .ExpectNoEvent(EventTypes.ExecutionStarted);
        Assert.Equal("not now", s.H.Last(EventTypes.ApprovalRejected)!.DataString("reason"));
    }

    [Fact]
    public void EditingReProposesUnderTheUsersName()
    {
        var mind = Proposing(Actions.CreateProject, "Atlas 2026 is ready.", ("name", "Atlas"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create a project called Atlas")
            .Edit(Actions.CreateProject, ("slug", "atlas-2026"), ("name", "Atlas 2026"))
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.CreateProject, "edited")
            .ExpectProposal(Actions.CreateProject, "pending")
            .Approve()
            .ExpectState(RelayState.Completed)
            .ExpectProject("atlas-2026")
            .ExpectProject("atlas", exists: false);
        var edited = s.H.Last(EventTypes.ProposalEdited)!;
        Assert.NotEqual(edited.DataString("fromProposalId"), edited.DataString("toProposalId"));
        var executedProposal = s.Response.Proposals.Single(p => p.Status == "executed");
        Assert.Equal(Producers.User, executedProposal.ProposedBy);
    }

    [Fact]
    public void DeletionIsAnApprovedActionThatKeepsASafetyCopy()
    {
        var mind = new ScriptedMind();
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note("The beta ships in the fourth quarter.")
            .FileLast("atlas");
        var atlas = s.H.Registry.FindActive("atlas")!;
        Assert.Contains(s.H.Index.Search("fourth quarter", null, 5), h => h.ProjectSlug == "atlas");
        mind.Always(request => request.Transcript[^1] switch
        {
            InputObserved => MindStep.Of(Propose(Actions.DeleteProject, "You asked for the project itself to go", ("projectId", atlas.Id), ("confirm", "delete")), "Proposing the deletion", Read(0.3)),
            ApprovalObserved { Granted: false } => MindStep.Of(Say("Atlas is still there."), "Left alone"),
            _ => MindStep.Of(Say("Atlas is gone."), "Deleted"),
        });

        s.Command("delete the Atlas project permanently")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.DeleteProject, "pending")
            .ExpectProject("atlas");
        // Nothing is gone until the words are approved; rejecting leaves everything in place.
        Assert.Equal("RequiresApproval", s.H.Last(EventTypes.ProposalDecided)!.DataString("tier"));
        s.Reject(Actions.DeleteProject).ExpectState(RelayState.Completed).ExpectProject("atlas").ExpectNoEvent(EventTypes.ProjectDeleted);

        s.Command("delete the Atlas project permanently")   // asked again, and approved this time
            .ExpectProposal(Actions.DeleteProject, "pending")
            .Approve()
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("executed")
            .ExpectEvent(EventTypes.ProjectDeleted)
            .ExpectProject("atlas", exists: false);
        var deleted = s.H.Registry.Find("atlas")!;
        Assert.Equal(ProjectRecord.DeletedStatus, deleted.Status);
        Assert.False(Directory.Exists(deleted.RootPath));
        var safety = s.H.Last(EventTypes.ProjectDeleted)!.DataString("safetyBackup")!;
        Assert.True(File.Exists(safety), "the safety zip must exist");
        Assert.StartsWith(s.H.Root.BackupsDirectory, safety, StringComparison.OrdinalIgnoreCase);
        using var zip = System.IO.Compression.ZipFile.OpenRead(safety);
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        // The registry keeps the tombstone and says so; nothing the project held can be found any more.
        Assert.Empty(s.H.Index.Search("fourth quarter", null, 5).Where(h => h.ProjectSlug == "atlas"));
    }

    [Fact]
    public void DeletionCanNeverComeFromSomethingOverheard()
    {
        var working = new ScriptedMind();
        var mind = new ListeningMind()
            .When("scrap the atlas project", "organize", "The user wants project Atlas deleted.")
            .Works(working);
        using var s = Scenario.New(_tmp, cfg => { Mind(cfg); cfg.Listening.Enabled = true; }, mind: mind).WithWorkspace()
            .Project("Atlas");
        var atlas = s.H.Registry.FindActive("atlas")!;
        working.Always(request => request.Transcript[^1] is InputObserved
            ? MindStep.Of(Propose(Actions.DeleteProject, "they said to scrap it", ("projectId", atlas.Id), ("confirm", "delete")), "Proposing the deletion", Read(0.3))
            : MindStep.Of(Say("Nothing was deleted."), "Left alone"));

        s.StartListening()
            .Listen("Honestly we should just scrap the atlas project.")
            .ExpectProject("atlas")
            .ExpectNoEvent(EventTypes.ProjectDeleted)
            .StopListening();
        _output.WriteLine(s.Transcript());

        // It never reached policy, and there was no card for the user to approve by accident: deletion is not
        // among the actions an overheard task is offered at all, and the mind is told so in its own transcript.
        Assert.DoesNotContain(s.H.Records(), r => r.Type == EventTypes.ProposalReceived && r.DataString("action") == Actions.DeleteProject);
        Assert.DoesNotContain(Actions.DeleteProject, working.Requests[0].Context.Actions.Select(a => a.Action));
        Assert.Contains(working.Requests[^1].Transcript.OfType<SystemObserved>(),
            o => o.Text.Contains("not an action you may propose here", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAskBoxRunsInTheForegroundFromIdleAndFromACompletedReceipt()
    {
        var mind = new ScriptedMind().Always(_ => MindStep.Of(Say("You have one project, Atlas."), "Answered"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .ExpectState(RelayState.Completed)
            .Ask("what projects do I have?")                           // asked from the COMPLETED receipt: the receipt is dismissed and the task runs
            .ExpectState(RelayState.Completed)
            .ExpectTask(TaskKind.Answer, TaskStatus.Completed, TaskOrigin.Direct)
            .ExpectAnswerContains("Atlas")
            .ExpectNoProposals();
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.StateChanged && r.DataString("from") == "COMPLETED" && r.DataString("to") == "IDLE");
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.StateChanged && r.DataString("from") == "IDLE" && r.DataString("to") == "PLANNING");
        s.Dismiss().ExpectState(RelayState.Idle)
            .Ask("and how many is that?")                             // and from IDLE
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("Atlas");
        Assert.Equal(2, s.H.Records().Count(r => r.Type == EventTypes.AskRecorded));
        Assert.Equal(2, s.H.Records().Count(r => r.Type == EventTypes.TaskCompleted && r.DataString("lane") == "ask"));
        Assert.DoesNotContain("Nothing to start planning", s.Snap.Notice ?? "");
    }

    [Fact]
    public void CancelMidStepReturnsToIdleAndIgnoresTheLateStep()
    {
        var pausing = new PausingMind();
        using var s = Scenario.New(_tmp, Mind, mind: pausing).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.Planning)
            .Cancel()
            .ExpectState(RelayState.Idle)
            .ExpectEvent(EventTypes.TaskCancelled);
        Assert.True(pausing.LastToken.IsCancellationRequested);
        Assert.False(pausing.Release()); // the gate was cancelled; nothing to release
        Assert.Equal(RelayState.Idle, s.Snap.State);
        Assert.Equal("planning", s.H.Last(EventTypes.TaskCancelled)!.DataString("stage"));
        Assert.Equal(0, s.H.Count(EventTypes.MindStepped));   // the step that arrived late is not a step of anything
        Assert.Equal(0, s.H.Count(EventTypes.LoopEnded));
        Assert.Empty(Directory.GetFiles(s.H.Root.TasksDirectory, "*.live.json"));
    }

    [Fact]
    public void AStepThatTakesTooLongFailsTheTaskWithAnIncident()
    {
        var pausing = new PausingMind();
        using var s = Scenario.New(_tmp, cfg => { Mind(cfg); cfg.Orchestrator.StepTimeoutMs = 5000; }, mind: pausing).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.Planning)
            .Advance(TimeSpan.FromSeconds(6))
            .ExpectState(RelayState.Failed)
            .ExpectEvent(EventTypes.TaskFailed);
        var incident = s.Snap.Incident!;
        Assert.Equal("mind_timeout", incident.Kind);
        Assert.Contains("did not answer within 5s", incident.Summary);
        Assert.False(s.Snap.CanRetry);
        Assert.True(pausing.IsPaused);   // the step is still held; the task stopped waiting for it
        s.Dismiss().ExpectState(RelayState.Idle);
    }

    [Fact]
    public void AMindThatThrowsBecomesAFailedTaskNotACrash()
    {
        using var s = Scenario.New(_tmp, Mind, mind: new ThrowingMind())
            .Command("anything")
            .ExpectState(RelayState.Failed)
            .ExpectEvent(EventTypes.TaskFailed);
        Assert.Equal("mind_failed", s.H.Last(EventTypes.TaskFailed)!.DataString("failure"));
        Assert.Contains("model exploded", s.Snap.Incident!.Detail);
        s.Dismiss().ExpectState(RelayState.Idle);
    }

    [Fact]
    public void CancelWhileAwaitingApprovalRejectsEverything()
    {
        var mind = Proposing(Actions.CreateProject, "Atlas is ready.", ("name", "Atlas"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .Cancel()
            .ExpectState(RelayState.Idle)
            .ExpectEvent(EventTypes.ApprovalRejected)
            .ExpectEvent(EventTypes.TaskCancelled)
            .ExpectProject("atlas", exists: false);
        Assert.Equal("awaiting_approval", s.H.Last(EventTypes.TaskCancelled)!.DataString("stage"));
        Assert.Empty(s.Snap.LiveTasks);
    }

    [Fact]
    public void HotkeysAreRefusedWhileAProposalWaits()
    {
        var mind = Proposing(Actions.CreateProject, "Atlas is ready.", ("name", "Atlas"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .Note("this should not start")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectEvent(EventTypes.HotkeyRejected);
        Assert.Equal(TransitionTable.DecideProposalsFirst, s.Snap.Notice);
    }

    [Fact]
    public void CrashWhileAwaitingApprovalIsReportedAtNextStart()
    {
        var mind = Proposing(Actions.CreateProject, "Atlas is ready.", ("name", "Atlas"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.AwaitingApproval);
        Assert.Single(Directory.GetFiles(s.H.Root.TasksDirectory, "*.live.json"));
        s.CrashAndRestart()
            .ExpectState(RelayState.Idle)
            .ExpectEvent(EventTypes.TaskInterruptedFound)
            .ExpectReview(ReviewItemKind.TurnInterrupted)
            .ExpectProject("atlas", exists: false);
        Assert.Empty(Directory.GetFiles(s.H.Root.TasksDirectory, "*.live.json"));
        Assert.Single(Directory.GetFiles(s.H.Root.TasksDirectory, "*.interrupted.json"));
        Assert.Equal("awaiting_approval", s.H.Last(EventTypes.TaskInterruptedFound)!.DataString("stage"));
        Assert.Equal("direct", s.H.Last(EventTypes.TaskInterruptedFound)!.DataString("origin"));
    }

    [Fact]
    public void CleanExitDuringATaskRecordsCancellationNotACrash()
    {
        var mind = Proposing(Actions.CreateProject, "Atlas is ready.", ("name", "Atlas"));
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .Restart()
            .ExpectState(RelayState.Idle)
            .ExpectNoEvent(EventTypes.TaskInterruptedFound)
            .ExpectEvent(EventTypes.TaskCancelled);
        Assert.Equal("awaiting_approval", s.H.Last(EventTypes.TaskCancelled)!.DataString("stage"));
    }

    [Fact]
    public void InterruptedExecutionJournalIsSurfacedInReview()
    {
        _tmp.Root.EnsureLayout(new FixedClock(Harness.T0));
        Directory.CreateDirectory(_tmp.Root.ExecutionsDirectory);
        File.WriteAllText(Path.Combine(_tmp.Root.ExecutionsDirectory, "P1.json"),
            """{"proposalId":"P1","action":"archive_project","turnId":"T1","startedAt":"2026-09-04T11:59:00Z","target":{"projectId":"X"}}""");
        using var s = Scenario.New(_tmp)
            .ExpectState(RelayState.Idle)
            .ExpectEvent(EventTypes.ExecutionInterruptedFound)
            .ExpectReview(ReviewItemKind.ExecutionInterrupted);
        var journal = File.ReadAllText(Path.Combine(_tmp.Root.ExecutionsDirectory, "P1.json"));
        Assert.Contains("\"status\": \"interrupted\"", journal);
        // A second start must not report it again.
        s.Restart();
        Assert.Equal(1, s.H.Count(EventTypes.ExecutionInterruptedFound));
    }

    [Fact]
    public void UserInitiatedOperationsTakeTheSamePathAndAreVisible()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Do("Create project from UI", c => Assert.True(c.CreateProject("Field Notes")))
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("executed")
            .ExpectProject("field-notes")
            .ExpectEvent(EventTypes.ProposalReceived)
            .ExpectEvent(EventTypes.ApprovalGranted)
            .ExpectEvent(EventTypes.ExecutionCompleted);
        Assert.True(s.H.Last(EventTypes.ApprovalGranted)!.DataBool("implicitViaUi"));
        Assert.Equal("user_operation", s.H.Last(EventTypes.TaskCreated)!.DataString("lane"));
        Assert.Equal("direct", s.H.Last(EventTypes.TaskCreated)!.DataString("origin"));

        s.Do("Archive from UI", c => Assert.True(c.ArchiveProject(s.H.Registry.FindActive("field-notes")!.Id)))
            .ExpectState(RelayState.Completed)
            .ExpectProject("field-notes", exists: false);

        s.Do("Duplicate slug is refused", c => Assert.False(c.CreateProject("Field Notes 2", "bad slug!")))
            .ExpectState(RelayState.Completed);
        Assert.Contains("not a valid slug", s.Snap.Notice);
    }

    [Fact]
    public void ExportBackupProducesAVerifiedZip()
    {
        var mind = Proposing(Actions.ExportBackup, "The backup is written and verified.");
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Command("make me a backup")
            .ExpectProposal(Actions.ExportBackup, "pending")
            .Approve()
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.BackupExported)
            .ExpectEvent(EventTypes.BackupVerified);
        Assert.True(s.H.Last(EventTypes.BackupVerified)!.DataBool("ok"));
        Assert.Single(Directory.GetFiles(s.H.Root.BackupsDirectory, "*.zip"));
    }

    [Fact]
    public void WhatWasFiledInOneSessionIsStillThereAndFindableInTheNext()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Project("Atlas")
            .Note("The beta ships in the fourth quarter.")
            .FileLast("atlas")
            .Restart()
            .ExpectState(RelayState.Idle)
            .ExpectProject("atlas");
        // The index is built from what is on disk at every start, so the note carries across without being re-filed.
        var hit = Assert.Single(s.H.Index.Search("fourth quarter", null, 5), h => h.ProjectSlug == "atlas");
        Assert.Contains("fourth quarter", hit.Text.Length > 0 ? hit.Text : hit.Excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(s.Snap.Inbox);
    }

    [Fact]
    public void WithTheModeOffAnInstructionIsRecordedAndNothingElseHappens()
    {
        using var s = Scenario.New(_tmp, x => x.Orchestrator.Mode = OrchestratorSettings.Off).WithWorkspace()
            .Command("create a project called Atlas")
            .ExpectState(RelayState.Completed)
            .ExpectNoEvent(EventTypes.TaskCreated)
            .ExpectReview(ReviewItemKind.RecordedInstruction)
            .ExpectProject("atlas", exists: false);
        Assert.Equal(OrchestratorSettings.Off, s.H.Last(EventTypes.CommandRecorded)!.DataString("orchestrator"));
    }

    public void Dispose() => _tmp.Dispose();
}
