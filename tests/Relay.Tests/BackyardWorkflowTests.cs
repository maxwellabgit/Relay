using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// The README's Backyard workflow: "move the backyard notes into Garden" when Garden does not exist yet.
/// One task, one operation graph: create_project as the prerequisite and one move_note per note that
/// depends on it. Each proposal is approved on its own; a dependent cannot be approved once its
/// prerequisite was rejected, nothing runs before the prerequisite has run, and every move is checked
/// again against the real registry when it executes.
/// </summary>
public class BackyardWorkflowTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private static readonly string[] BackyardNotes =
    [
        "Idea: build a pergola over the backyard patio.",
        "Task: clear out the backyard shed before winter.",
        "We decided the backyard fence gets replaced in spring.",
    ];

    /// <summary>A Home project holding the three backyard notes and one unrelated note.</summary>
    private static Scenario Seeded(TempRoot tmp)
    {
        var s = Scenario.New(tmp).WithWorkspace()
            .Command("create project Home").Approve().ExpectProject("home");
        foreach (var text in BackyardNotes) s.Note(text);
        s.Note("Idea: repaint the kitchen cabinets.");
        return s.Command("file all notes under Home").ExpectState(RelayState.Completed);
    }

    private static IReadOnlyList<NoteDocument> NotesOf(Scenario s, string slug)
    {
        var project = s.H.Registry.FindActive(slug) ?? throw s.Fail($"No active project {slug}");
        return ProjectNoteStore.ReadAll(project.RootPath).Notes.Select(n => n.Note).ToList();
    }

    [Fact]
    public void MovingNotesIntoAProjectThatDoesNotExistIsOneGraphOfDependentProposals()
    {
        using var s = Seeded(_tmp);
        Assert.Equal(4, NotesOf(s, "home").Count);

        s.Command("move the backyard notes into Garden").ExpectState(RelayState.AwaitingApproval);

        var proposals = s.Response.Proposals;
        var create = Assert.Single(proposals, p => p.Action == Actions.CreateProject);
        var moves = proposals.Where(p => p.Action == Actions.MoveNote).ToList();
        Assert.Equal(3, moves.Count);
        Assert.Equal("garden", create.Target["slug"]);
        Assert.Empty(create.DependsOn);
        Assert.All(moves, m =>
        {
            Assert.Equal([create.ProposalId], m.DependsOn);
            Assert.Equal("pending", m.Status);
            Assert.Null(m.BlockedBy);
            Assert.Equal("garden", m.Target["toProjectSlug"]);
            Assert.False(m.Target.ContainsKey("toProjectId"));                      // nothing to point at yet
            Assert.Contains(m.Reasons, r => r.Contains("does not exist yet") && r.Contains("checked again when it runs"));
        });
        // The unrelated note was not swept up.
        var kitchen = NotesOf(s, "home").Single(n => n.Body.Contains("kitchen"));
        Assert.DoesNotContain(moves, m => m.Target["noteId"] == kitchen.Id);
        // Nothing has run; the record is untouched.
        s.ExpectProject("garden", exists: false).ExpectNoEvent(EventTypes.NoteMoved);
        Assert.Contains(s.Response.Steps, step => step.Contains("depends on it"));
    }

    [Fact]
    public void ApprovingEverythingCreatesTheProjectFirstAndThenMovesEachNoteWithItsHistory()
    {
        using var s = Seeded(_tmp);
        var before = NotesOf(s, "home").Where(n => n.Body.Contains("backyard")).Select(n => n.Id).OrderBy(x => x).ToList();

        s.Command("move the backyard notes into Garden").ApproveAll()
            .ExpectState(RelayState.Completed).ExpectOutcome("executed")
            .ExpectProject("garden").ExpectEvent(EventTypes.ProjectCreated).ExpectEvent(EventTypes.NoteMoved, atLeast: 3);

        Assert.All(s.Response.Proposals, p => Assert.Equal("executed", p.Status));
        var garden = NotesOf(s, "garden").Select(n => n.Id).OrderBy(x => x).ToList();
        Assert.Equal(before, garden);                                              // the same notes, same ids, now in Garden
        var home = NotesOf(s, "home");
        Assert.Single(home);
        Assert.Contains("kitchen", home[0].Body);

        // Order in the ledger: the project exists before any note moves.
        var records = s.H.Records().ToList();
        var created = records.FindIndex(r => r.Type == EventTypes.ProjectCreated);
        var firstMove = records.FindIndex(r => r.Type == EventTypes.NoteMoved);
        Assert.True(created >= 0 && created < firstMove, "the project must be created before the first move");

        // The source keeps a pointer to where each note went, so the move is traceable from either side.
        var homeRoot = s.H.Registry.FindActive("home")!.RootPath;
        foreach (var id in before) Assert.True(File.Exists(Path.Combine(ProjectLayout.VersionsDirectory(homeRoot), id, "moved-to.txt")), $"no moved-to pointer for {id}");

        // The moved notes are searchable under Garden straight away.
        s.Command("what did I say about the backyard fence").ExpectAnswerContains("garden/");
    }

    [Fact]
    public void RejectingThePrerequisiteBlocksItsDependentsAndTheRefusalIsRecorded()
    {
        using var s = Seeded(_tmp);
        var executionsBefore = s.H.Count(EventTypes.ExecutionStarted);
        s.Command("move the backyard notes into Garden")
            .Reject(Actions.CreateProject, "not a new project");

        // The moves are still pending but cannot be approved: the project they need will not exist.
        s.ExpectState(RelayState.AwaitingApproval);
        var moves = s.Response.Proposals.Where(p => p.Action == Actions.MoveNote).ToList();
        Assert.All(moves, m => { Assert.Equal("pending", m.Status); Assert.Contains("Create project 'Garden' (rejected)", m.BlockedBy); });

        s.Approve(Actions.MoveNote);
        Assert.Contains("Create project 'Garden' (rejected)", s.Snap.Notice);
        Assert.All(s.Response.Proposals.Where(p => p.Action == Actions.MoveNote), m => Assert.Equal("pending", m.Status));
        s.ExpectEvent(EventTypes.ApprovalRefused).ExpectNoEvent(EventTypes.NoteMoved).ExpectProject("garden", exists: false);
        var refused = s.H.Last(EventTypes.ApprovalRefused)!;
        Assert.Equal(Actions.MoveNote, refused.DataString("action"));
        Assert.Contains("(rejected)", refused.DataString("reason"));

        // Rejecting the dependents closes the task; nothing ran.
        foreach (var _ in moves) s.Reject(Actions.MoveNote);
        s.ExpectState(RelayState.Completed).ExpectOutcome("rejected");
        Assert.Equal(executionsBefore, s.H.Count(EventTypes.ExecutionStarted));
        Assert.Equal(4, NotesOf(s, "home").Count);
    }

    [Fact]
    public void PartialApprovalMovesOnlyWhatWasApproved()
    {
        using var s = Seeded(_tmp);
        var executionsBefore = s.H.Count(EventTypes.ExecutionStarted);
        s.Command("move the backyard notes into Garden");

        // Approve a move before its prerequisite: allowed, but nothing runs until the whole set is decided.
        s.Approve(Actions.MoveNote).ExpectState(RelayState.AwaitingApproval);
        Assert.Equal(executionsBefore, s.H.Count(EventTypes.ExecutionStarted));
        s.Reject(Actions.MoveNote, "this one stays");
        s.Approve(Actions.CreateProject).ExpectState(RelayState.AwaitingApproval);   // one move still undecided
        s.Approve(Actions.MoveNote).ExpectState(RelayState.Completed).ExpectOutcome("executed").ExpectProject("garden");

        Assert.Equal(2, NotesOf(s, "garden").Count);
        Assert.Equal(2, NotesOf(s, "home").Count);                                 // kitchen + the one that stayed
        var stayed = s.Response.Proposals.Single(p => p.Status == "rejected");
        Assert.Contains(NotesOf(s, "home"), n => n.Id == stayed.Target["noteId"]);
        Assert.Equal(3, s.Response.Proposals.Count(p => p.Status == "executed"));
    }

    [Fact]
    public void RenamingTheNewProjectInTheEditCarriesTheMovesWithIt()
    {
        using var s = Seeded(_tmp).Command("move the backyard notes into Garden")
            .Edit(Actions.CreateProject, ("name", "Yard"), ("slug", "yard"));

        var moves = s.Response.Proposals.Where(p => p.Action == Actions.MoveNote && p.Status == "pending").ToList();
        Assert.Equal(3, moves.Count);
        var replacement = s.Response.Proposals.Single(p => p.Action == Actions.CreateProject && p.Status == "pending");
        Assert.All(moves, m =>
        {
            Assert.Equal([replacement.ProposalId], m.DependsOn);                    // dependents follow the replacement
            Assert.Equal("yard", m.Target["toProjectSlug"]);                        // and its new name
            Assert.Null(m.BlockedBy);
        });

        s.ApproveAll().ExpectState(RelayState.Completed).ExpectOutcome("executed").ExpectProject("yard").ExpectProject("garden", exists: false);
        Assert.Equal(3, NotesOf(s, "yard").Count);
    }

    [Fact]
    public void WhenTheDestinationExistsThereIsNoPrerequisiteAndNotesAlreadyThereAreLeftAlone()
    {
        using var s = Seeded(_tmp).Command("create project Garden").Approve()
            .Command("move the backyard notes into Garden");

        var proposals = s.Response.Proposals;
        Assert.DoesNotContain(proposals, p => p.Action == Actions.CreateProject);
        Assert.Equal(3, proposals.Count(p => p.Action == Actions.MoveNote));
        Assert.All(proposals, p => { Assert.Empty(p.DependsOn); Assert.Equal(s.H.Registry.FindActive("garden")!.Id, p.Target["toProjectId"]); });

        s.ApproveAll().ExpectOutcome("executed");
        Assert.Equal(3, NotesOf(s, "garden").Count);

        // Asking again finds every backyard note already in Garden: an answer, not a proposal.
        s.Command("move the backyard notes into Garden").ExpectState(RelayState.Completed).ExpectNoProposals()
            .ExpectAnswerContains("already in 'Garden'");
    }

    [Fact]
    public void ATopicNobodyWroteAboutMovesNothingAndCreatesNothing()
    {
        using var s = Seeded(_tmp)
            .Command("move the greenhouse notes into Garden").ExpectState(RelayState.Completed).ExpectNoProposals()
            .ExpectAnswerContains("nothing to move").ExpectAnswerContains("no reason to create 'Garden'")
            .ExpectProject("garden", exists: false);
    }

    [Fact]
    public void TheMoveGrammarReadsSeveralPhrasingsAndLeavesDraftFilingAlone()
    {
        using var s = Seeded(_tmp);
        foreach (var phrasing in new[]
        {
            "move everything about the backyard into a new project called Garden",
            "move all notes about backyard to Garden",
            "transfer the backyard notes to project Garden",
        })
        {
            s.Command(phrasing).ExpectState(RelayState.AwaitingApproval);
            Assert.Equal(1, s.Response.Proposals.Count(p => p.Action == Actions.CreateProject));
            Assert.Equal(3, s.Response.Proposals.Count(p => p.Action == Actions.MoveNote));
            s.Cancel().ExpectState(RelayState.Idle);
        }

        // "move the notes into X" is filing drafts from the inbox, not a topic move.
        s.Note("Idea: a rain barrel by the backyard downspout.")
            .Command("move the notes into Home").ExpectState(RelayState.Completed);
        Assert.Contains(s.Response.Proposals, p => p.Action == Actions.RouteNote && p.Status == "executed");
        Assert.DoesNotContain(s.Response.Proposals, p => p.Action == Actions.MoveNote);
    }

    [Fact]
    public void AMoveTowardsAPlannedProjectIsDeniedAtRunTimeIfTheProjectIsStillMissing()
    {
        // Policy accepts a planned destination only while deciding; at execution the real registry is the judge.
        using var s = Seeded(_tmp);
        var home = s.H.Registry.FindActive("home")!;
        var note = NotesOf(s, "home").First();
        var target = new Dictionary<string, string> { ["projectId"] = home.Id, ["noteId"] = note.Id, ["toProject"] = "Garden" };
        PolicyWorld World(IReadOnlyList<string> planned) => new()
        {
            Registry = s.H.Registry, Roots = s.H.Roots, DataRoot = _tmp.Root, DraftNoteExists = _ => false,
            ProjectNoteExists = (projectId, noteId) => s.H.Registry.ById(projectId) is { } p && ProjectNoteStore.Find(p.RootPath, noteId) is not null,
            PlannedProjects = planned,
        };
        var proposal = new Proposal("01HZZZZZZZZZZZZZZZZZZZZZZ1", Actions.MoveNote, "test", target, ["01HZZZZZZZZZZZZZZZZZZZZZZ0"], [], Risks.ControlledWrite, true, Producers.Rules);

        var deciding = PolicyEngine.Decide(proposal, World(["garden"]));
        Assert.Equal(DecisionOutcome.NeedsApproval, deciding.Outcome);
        Assert.Equal("garden", deciding.NormalizedTarget["toProjectSlug"]);

        var running = PolicyEngine.Decide(proposal, World([]));
        Assert.Equal(DecisionOutcome.Deny, running.Outcome);
        Assert.Contains(running.Reasons, r => r.Contains("No active project matches 'Garden'"));
    }

    public void Dispose() => _tmp.Dispose();
}
