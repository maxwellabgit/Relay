using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

public class OrchestratorTurnTests : IDisposable
{
    private readonly TempRoot _tmp = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public OrchestratorTurnTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public void CreateProjectNeedsApprovalThenExecutesThroughTheFullPath()
    {
        // The deterministic grammar, named explicitly: mind is the default, and a mindless default has a Review notice of its own.
        using var s = Scenario.New(_tmp, x => x.Orchestrator.Mode = OrchestratorSettings.Rules).WithWorkspace()
            .Command("Create a project called Market Study")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.CreateProject, "pending")
            .ExpectProject("market-study", exists: false);
        // The pending proposal is shown once, in Response next to the plan; Review is for everything else.
        Assert.Single(s.Snap.PendingProposals);
        Assert.Empty(s.Snap.Review);
        s.Approve(Actions.CreateProject)
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("executed")
            .ExpectProposal(Actions.CreateProject, "executed")
            .ExpectProject("market-study");

        var types = s.H.Records().Select(r => r.Type).ToList();
        var expectedOrder = new[]
        {
            EventTypes.CaptureCommitted, EventTypes.CommandRecorded, EventTypes.TaskCreated, EventTypes.TaskPlanned, EventTypes.ProposalReceived,
            EventTypes.ProposalDecided, EventTypes.ApprovalGranted, EventTypes.ExecutionStarted, EventTypes.ProjectCreated, EventTypes.ExecutionCompleted, EventTypes.TaskCompleted,
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
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ProposalReceived && r.DataString("proposedBy") == "rules");
    }

    [Fact]
    public void WithoutAProjectFolderCreateProjectIsDeniedVisibly()
    {
        using var s = Scenario.New(_tmp)
            .Command("create project Atlas")
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
    public void NewProjectInAFolderRegistersItOnceAndLaterVoiceCommandsReuseIt()
    {
        var folder = Path.Combine(Path.GetDirectoryName(_tmp.Root.Path)!, Path.GetFileName(_tmp.Root.Path) + "-projects");
        Directory.CreateDirectory(folder);
        try
        {
            using var s = Scenario.New(_tmp);
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

            // By voice, with no folder given, the registered folder is the default.
            s.Command("create project Harbor").Approve()
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
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas")
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
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas")
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
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("remember that Atlas ships in Q4")
            .Command("file the last note under Atlas")
            .Command("permanently delete project Atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.DeleteProject, "pending")
            .ExpectProject("atlas");
        // Nothing is gone until the words are approved; rejecting leaves everything in place.
        Assert.Equal("RequiresApproval", s.H.Last(EventTypes.ProposalDecided)!.DataString("tier"));
        s.Reject(Actions.DeleteProject).ExpectState(RelayState.Completed).ExpectProject("atlas").ExpectNoEvent(EventTypes.ProjectDeleted);

        s.Command("delete project Atlas forever")
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
        // The registry keeps the tombstone and says so; nothing else about the project remains.
        s.Command("list projects").ExpectState(RelayState.Completed);
        Assert.Contains("deleted", s.Response.Answer!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(s.H.Index.Search("ships in Q4", null, 5).Where(h => h.ProjectSlug == "atlas"));
    }

    [Fact]
    public void DeletionCanNeverComeFromSomethingOverheard()
    {
        // The grammar handles the direct command; the canned planner stands in for the model on the judge's focused prompt.
        var canned = new CannedOrchestrator().Otherwise((req, ctx) => new TurnPlan(true, "Overheard deletion", [], null, [],
            [new Proposal("P-del", Actions.DeleteProject, "they said to delete it", new Dictionary<string, string> { ["projectId"] = ctx.Registry.FindActive("atlas")!.Id, ["confirm"] = "delete" }, [req.SourceEventId], [], Risks.ControlledWrite, true, Producers.Model)], "canned"));
        var judge = new ScriptedJudge().When("scrap the atlas project", Relay.Core.Tasks.TaskKind.Organize, "The user wants project Atlas deleted.");
        using var s = Scenario.New(_tmp, orchestrator: new CompositeOrchestrator(new RuleBasedOrchestrator(), canned), judge: judge, configure: x => x.Judge.Mode = JudgeSettings.Heuristic).WithWorkspace()
            .Command("create project Atlas").Approve()
            .StartListening()
            .Hear("Honestly we should just scrap the atlas project.")
            .Observe()
            .ExpectAnyProposal(Actions.DeleteProject, "denied")
            .ExpectProject("atlas")
            .ExpectNoEvent(EventTypes.ProjectDeleted)
            .StopListening();
        var decided = s.H.Records().Last(r => r.Type == EventTypes.ProposalDecided && r.DataString("action") == Actions.DeleteProject);
        Assert.Contains("direct request", string.Join(" ", decided.Data.GetProperty("reasons").EnumerateArray().Select(e => e.GetString())));
    }

    [Fact]
    public void ArchiveIsRecoverableAndRestoreVerifiesTheManifest()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve().ExpectProject("atlas")
            .Command("archive project atlas")
            .ExpectProposal(Actions.ArchiveProject, "pending")
            .Approve()
            .ExpectState(RelayState.Completed)
            .ExpectProject("atlas", exists: false)
            .ExpectEvent(EventTypes.ProjectArchived);
        var archived = s.H.Registry.Find("atlas")!;
        Assert.Equal(ProjectRecord.ArchivedStatus, archived.Status);
        Assert.True(Directory.Exists(archived.ArchivedPath));
        Assert.True(File.Exists(archived.ArchivedPath + ".manifest.json"));

        s.Command("restore project atlas").ExpectProposal(Actions.RestoreProject, "pending").Approve()
            .ExpectState(RelayState.Completed)
            .ExpectProject("atlas")
            .ExpectEvent(EventTypes.ProjectRestored);
        var restore = s.H.Last(EventTypes.ProjectRestored)!;
        Assert.Equal(System.Text.Json.JsonValueKind.Array, restore.Data.GetProperty("problems").ValueKind);
        Assert.Equal(0, restore.Data.GetProperty("problems").GetArrayLength());
    }

    [Fact]
    public void RememberFilesAndRecallWorkWithoutApproval()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("Remember that the Atlas launch moves to October 14")
            .ExpectState(RelayState.Completed)
            .ExpectOutcome("executed")
            .ExpectProposal(Actions.CreateDraftNote, "executed");
        Assert.Single(s.H.Notes.Unrouted());
        Assert.Equal(1, s.H.Count(EventTypes.ApprovalGranted)); // only the create_project approval

        s.Command("file the last note under Atlas")
            .ExpectState(RelayState.Completed)
            .ExpectProposal(Actions.RouteNote, "executed")
            .ExpectEvent(EventTypes.NoteRouted)
            .ExpectEvent(EventTypes.NoteWritten);
        Assert.Empty(s.H.Notes.Unrouted());
        var project = s.H.Registry.FindActive("atlas")!;
        var (notes, problems) = ProjectNoteStore.ReadAll(project.RootPath);
        Assert.Empty(problems);
        var note = Assert.Single(notes).Note;
        Assert.Contains("October 14", note.Body);
        Assert.Equal(project.Id, note.ProjectId);
        var span = Assert.Single(note.Spans);
        var source = s.H.Records().First(r => r.Type == EventTypes.CaptureCommitted && r.DataString("text")!.Contains("Remember"));
        Assert.Equal(source.Id, span.EventId);
        Assert.Equal(note.Body, source.DataString("text")![span.Start..span.End]); // the span is exactly the remembered words

        s.Command("What did I say about the launch?")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("October 14")
            .ExpectNoProposals()
            .ExpectEvent(EventTypes.ToolCalled)
            .ExpectEvent(EventTypes.ToolReturned);
        Assert.Contains(s.Response.Citations, c => c.Kind == "note" && c.Id == note.Id && c.ProjectSlug == "atlas");
    }

    [Fact]
    public void RecallCitesTheOriginalCaptureSpan()
    {
        using var s = Scenario.New(_tmp)
            .Note("Pricing idea: three tiers, and annual billing gets fifteen percent off.")
            .ExpectState(RelayState.Completed)
            .Command("what did I say about annual billing")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("fifteen percent");
        var citation = s.Response.Citations.First();
        var capture = s.H.Records().First(r => r.Type == EventTypes.CaptureCommitted && r.DataString("mode") == "note");
        Assert.NotNull(citation.Span);
        Assert.Equal(capture.Id, citation.Span!.EventId);
        var text = capture.DataString("text")!;
        Assert.Contains("annual billing", text[citation.Span.Start..citation.Span.End], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListProjectsAnswersFromTheRegistry()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("create project Beacon").Approve()
            .Command("list my projects")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("Atlas")
            .ExpectAnswerContains("beacon")
            .ExpectNoProposals();
    }

    [Fact]
    public void TheAskBoxPlansInTheForegroundFromIdleAndFromACompletedReceipt()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .ExpectState(RelayState.Completed)
            .Ask("list my projects")                                  // asked from the COMPLETED receipt: the receipt is dismissed and the ask plans
            .ExpectState(RelayState.Completed)
            .ExpectTask(TaskKind.Answer, TaskStatus.Completed, TaskOrigin.Direct)
            .ExpectAnswerContains("Atlas")
            .ExpectNoProposals();
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.StateChanged && r.DataString("from") == "COMPLETED" && r.DataString("to") == "IDLE");
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.StateChanged && r.DataString("from") == "IDLE" && r.DataString("to") == "PLANNING");
        s.Dismiss().ExpectState(RelayState.Idle)
            .Ask("list my projects")                                  // and from IDLE
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("Atlas");
        Assert.Equal(2, s.H.Records().Count(r => r.Type == EventTypes.AskRecorded));
        Assert.Equal(2, s.H.Records().Count(r => r.Type == EventTypes.TaskCompleted && r.DataString("lane") == "ask"));
        Assert.DoesNotContain("Nothing to start planning", s.Snap.Notice ?? "");
    }

    [Fact]
    public void UnknownInstructionsAreAnsweredNotExecuted()
    {
        using var s = Scenario.New(_tmp)
            .Command("Please rearrange the deck chairs")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("did not understand")
            .ExpectNoProposals()
            .ExpectNoEvent(EventTypes.ExecutionStarted);
        Assert.False(s.H.Last(EventTypes.TaskPlanned)!.DataBool("understood"));
    }

    [Fact]
    public void CancelDuringPlanningReturnsToIdleAndIgnoresTheLatePlan()
    {
        var pausing = new PausingOrchestrator(new RuleBasedOrchestrator());
        using var s = Scenario.New(_tmp, orchestrator: pausing).WithWorkspace()
            .Command("create project Atlas")
            .ExpectState(RelayState.Planning)
            .Cancel()
            .ExpectState(RelayState.Idle)
            .ExpectEvent(EventTypes.TaskCancelled);
        Assert.True(pausing.LastToken.IsCancellationRequested);
        Assert.False(pausing.Release()); // the gate was cancelled; nothing to release
        Assert.Equal(RelayState.Idle, s.Snap.State);
        Assert.Equal(0, s.H.Count(EventTypes.TaskPlanned));
        Assert.Empty(Directory.GetFiles(s.H.Root.TasksDirectory, "*.live.json"));
    }

    [Fact]
    public void PlanningTimeoutFailsTheTaskWithAnIncident()
    {
        var pausing = new PausingOrchestrator(new RuleBasedOrchestrator());
        using var s = Scenario.New(_tmp, orchestrator: pausing, configure: x => x.Orchestrator.PlanningTimeoutMs = 5000).WithWorkspace()
            .Command("create project Atlas")
            .ExpectState(RelayState.Planning)
            .Advance(TimeSpan.FromSeconds(6))
            .ExpectState(RelayState.Failed)
            .ExpectEvent(EventTypes.TaskFailed);
        Assert.Equal("planning_timeout", s.Snap.Incident!.Kind);
        Assert.False(s.Snap.CanRetry);
        s.Dismiss().ExpectState(RelayState.Idle);
    }

    [Fact]
    public void OrchestratorExceptionsBecomeAFailedTaskNotACrash()
    {
        using var s = Scenario.New(_tmp, orchestrator: new ThrowingOrchestrator())
            .Command("anything")
            .ExpectState(RelayState.Failed)
            .ExpectEvent(EventTypes.TaskFailed);
        Assert.Contains("model exploded", s.Snap.Incident!.Detail);
        s.Dismiss().ExpectState(RelayState.Idle);
    }

    [Fact]
    public void CancelWhileAwaitingApprovalRejectsEverything()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .Cancel()
            .ExpectState(RelayState.Idle)
            .ExpectEvent(EventTypes.ApprovalRejected)
            .ExpectEvent(EventTypes.TaskCancelled)
            .ExpectProject("atlas", exists: false);
    }

    [Fact]
    public void HotkeysAreRefusedWhileAProposalWaits()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas")
            .ExpectState(RelayState.AwaitingApproval)
            .Note("this should not start")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectEvent(EventTypes.HotkeyRejected);
        Assert.Equal(TransitionTable.DecideProposalsFirst, s.Snap.Notice);
    }

    [Fact]
    public void CrashWhileAwaitingApprovalIsReportedAtNextStart()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas")
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
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas")
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
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("export a backup")
            .ExpectProposal(Actions.ExportBackup, "pending")
            .Approve()
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.BackupExported)
            .ExpectEvent(EventTypes.BackupVerified);
        Assert.True(s.H.Last(EventTypes.BackupVerified)!.DataBool("ok"));
        Assert.Single(Directory.GetFiles(s.H.Root.BackupsDirectory, "*.zip"));
    }

    [Fact]
    public void CrossSessionContinuity()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("remember that Atlas ships in Q4")
            .Command("file the last note under atlas")
            .Restart()
            .ExpectState(RelayState.Idle)
            .Command("list projects").ExpectAnswerContains("atlas")
            .Command("what did I say about Q4").ExpectAnswerContains("ships in Q4");
        Assert.Contains(s.Response.Citations, c => c.Kind == "note" && c.ProjectSlug == "atlas");
        Assert.DoesNotContain(s.Response.Citations, c => c.Kind == "capture");        // the note cites its capture as the source span; the passage is not listed twice
        _output.WriteLine(s.Transcript()); // the reviewable artifact: how the orchestrator handled each prompt
    }

    [Fact]
    public void CannedPlansDriveTheSamePipeline()
    {
        var canned = new CannedOrchestrator()
            .On("do the thing", (req, _) => new TurnPlan(true, "Canned plan", ["step one"], "Done thinking.", [],
                [new Proposal("P-canned", Actions.CreateProject, "because", new Dictionary<string, string> { ["name"] = "Canned Project" }, [req.SourceEventId], ["folder"], Risks.ControlledWrite, false, Producers.Model)],
                "canned"));
        using var s = Scenario.New(_tmp, orchestrator: canned).WithWorkspace()
            .Command("do the thing")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.CreateProject, "pending");
        // The model claimed no approval was needed; policy overrode it and said so.
        Assert.Contains(s.Response.Proposals.Single().Reasons, r => r.Contains("claimed no approval"));
        s.Approve().ExpectState(RelayState.Completed).ExpectProject("canned-project");
        Assert.Single(canned.Requests);
    }

    [Fact]
    public void ProposalsWithoutSourcesAreDenied()
    {
        var canned = new CannedOrchestrator()
            .On("x", new TurnPlan(true, "no source", [], null, [],
                [new Proposal("P-nosrc", Actions.CreateProject, "r", new Dictionary<string, string> { ["name"] = "Nope" }, [], [], Risks.ControlledWrite, true, Producers.Model)], "canned"));
        using var s = Scenario.New(_tmp, orchestrator: canned).WithWorkspace()
            .Command("x")
            .ExpectState(RelayState.Completed)
            .ExpectProposal(Actions.CreateProject, "denied")
            .ExpectProject("nope", exists: false);
        Assert.Contains(s.Response.Proposals.Single().Reasons, r => r.Contains("no source"));
    }

    [Fact]
    public void OrchestratorOffOnlyRecordsInstructions()
    {
        using var s = Scenario.New(_tmp, configure: x => x.Orchestrator.Mode = OrchestratorSettings.Off).WithWorkspace()
            .Command("create project Atlas")
            .ExpectState(RelayState.Completed)
            .ExpectNoEvent(EventTypes.TaskCreated)
            .ExpectReview(ReviewItemKind.RecordedInstruction)
            .ExpectProject("atlas", exists: false);
    }

    public void Dispose() => _tmp.Dispose();
}

public class PolicyAndExecutorTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Theory]
    [InlineData(Actions.CreateDraftNote, Tier.Automatic)]
    [InlineData(Actions.RouteNote, Tier.Automatic)]
    [InlineData(Actions.CreateProject, Tier.RequiresApproval)]
    [InlineData(Actions.ModifyNote, Tier.RequiresApproval)]
    [InlineData(Actions.ArchiveProject, Tier.RequiresApproval)]
    [InlineData(Actions.LaunchWorker, Tier.RequiresApproval)]
    [InlineData(Actions.DeleteProject, Tier.RequiresApproval)]
    [InlineData(Actions.ModelRequest, Tier.RequiresApproval)]
    [InlineData(Actions.UpdatePreference, Tier.RequiresApproval)]
    [InlineData(Actions.RunShell, Tier.Prohibited)]
    [InlineData("format_disk", Tier.Prohibited)]
    public void TiersAreFixedByAction(string action, Tier tier) => Assert.Equal(tier, PolicyEngine.TierOf(action));

    [Fact]
    public void ProposalHashCoversActionTargetAndSourcesOnly()
    {
        var a = new Proposal("1", Actions.CreateProject, "r1", new Dictionary<string, string> { ["name"] = "X", ["slug"] = "x" }, ["e1"], ["eff"], Risks.ControlledWrite, true, Producers.Rules);
        var b = a with { ProposalId = "2", Reason = "different", ExpectedEffects = [] };
        var c = a with { Target = new Dictionary<string, string> { ["slug"] = "x", ["name"] = "X" } };
        var d = a with { Target = new Dictionary<string, string> { ["name"] = "Y", ["slug"] = "x" } };
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.Hash(), c.Hash());
        Assert.NotEqual(a.Hash(), d.Hash());
    }

    [Fact]
    public void CapabilitiesAreSingleUseAndBoundToTheProposalHash()
    {
        var issuer = new CapabilityIssuer();
        var p = new Proposal("1", Actions.CreateProject, "r", new Dictionary<string, string> { ["name"] = "X" }, ["e1"], [], Risks.ControlledWrite, true, Producers.Rules);
        var cap = issuer.Issue(p, Harness.T0);
        Assert.True(issuer.Consume(cap, p, Harness.T0).Ok);
        Assert.Contains("already used", issuer.Consume(cap, p, Harness.T0).Reason);

        var cap2 = issuer.Issue(p, Harness.T0);
        var changed = p with { Target = new Dictionary<string, string> { ["name"] = "Y" } };
        Assert.Contains("changed after approval", issuer.Consume(cap2, changed, Harness.T0).Reason);
        Assert.Contains("expired", issuer.Consume(cap2, p, Harness.T0 + TimeSpan.FromHours(1)).Reason);
        var forged = cap2 with { Signature = new string('0', 64) };
        Assert.Contains("signature", issuer.Consume(forged, p, Harness.T0).Reason);
        Assert.False(new CapabilityIssuer().Consume(cap2, p, Harness.T0).Ok); // another session's key
    }

    [Fact]
    public void ExecutorRechecksPolicyAtExecutionTime()
    {
        using var h = new Harness(_tmp.Root).Start();
        var ws = Path.Combine(Path.GetDirectoryName(_tmp.Root.Path)!, Path.GetFileName(_tmp.Root.Path) + "-ws2");
        Directory.CreateDirectory(ws);
        try
        {
            h.Coordinator.RegisterWorkspace(ws);
            var issuer = new CapabilityIssuer();
            var executor = new Executor(_tmp.Root, h.Registry, h.Roots, h.Notes, h.Clock, issuer);
            var world = new PolicyWorld { Registry = h.Registry, Roots = h.Roots, DataRoot = _tmp.Root, DraftNoteExists = _ => false, ProjectNoteExists = (_, _) => false };
            var p = new Proposal("1", Actions.CreateProject, "r", new Dictionary<string, string> { ["name"] = "Atlas" }, ["e1"], [], Risks.ControlledWrite, true, Producers.Rules);
            Assert.Equal(DecisionOutcome.NeedsApproval, PolicyEngine.Decide(p, world).Outcome);
            var cap = issuer.Issue(p, h.Clock.UtcNow);

            // The world changes between approval and execution: the target folder appears with content.
            Directory.CreateDirectory(Path.Combine(ws, "atlas"));
            File.WriteAllText(Path.Combine(ws, "atlas", "stray.txt"), "x");

            var sink = new RecordingSink();
            var result = executor.Execute(p, cap, world, "T", sink);
            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Contains("Preconditions no longer hold", result.Error);
            Assert.Contains(sink.Events, e => e.Type == EventTypes.ExecutionFailed && e.Stage == "recheck");
            Assert.DoesNotContain(sink.Events, e => e.Type == EventTypes.ExecutionStarted);
            Assert.Null(h.Registry.FindActive("atlas"));
        }
        finally { Directory.Delete(ws, recursive: true); }
    }

    private sealed class RecordingSink : IExecutionSink
    {
        public List<(string Type, string? Stage)> Events { get; } = new();
        public LedgerRecord? Record(string type, object data)
        {
            var stage = data.GetType().GetProperty("stage")?.GetValue(data) as string;
            Events.Add((type, stage));
            return null;
        }
    }

    public void Dispose() => _tmp.Dispose();
}
