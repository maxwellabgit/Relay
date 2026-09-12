using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Search;
using Relay.Core.State;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>
/// The README's Backyard workflow: "move the backyard notes into Garden" when Garden does not exist yet.
/// A note cannot go anywhere until the place exists, so the project is proposed first and every operation
/// after it is decided on its own — nothing runs before the project has, a refusal leaves what needed it
/// with nowhere to go, and every move is checked again against the real registry when it executes. The
/// mind reading the instruction is scripted; everything after it is the production path.
/// </summary>
public class BackyardWorkflowTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private const string Topic = "backyard";
    private const string Destination = "Garden";

    private static readonly string[] BackyardNotes =
    [
        "Idea: build a pergola over the backyard patio.",
        "Task: clear out the backyard shed before winter.",
        "We decided the backyard fence gets replaced in spring.",
    ];

    /// <summary>Mind mode with the model on and the note chord dictating: the instruction is the mind's, the notes are not.</summary>
    private static void Mind(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    /// <summary>A Home project holding the three backyard notes and one unrelated note.</summary>
    private static Scenario Seeded(TempRoot tmp, IMind? mind = null)
    {
        var s = Scenario.New(tmp, Mind, mind: mind).WithWorkspace().Project("Home");
        foreach (var text in BackyardNotes) s.Note(text);
        s.Note("Idea: repaint the kitchen cabinets.");
        return s.FileAll("home").ExpectState(RelayState.Completed);
    }

    private static IReadOnlyList<NoteDocument> NotesOf(Scenario s, string slug)
    {
        var project = s.H.Registry.FindActive(slug) ?? throw s.Fail($"No active project {slug}");
        return ProjectNoteStore.ReadAll(project.RootPath).Notes.Select(n => n.Note).ToList();
    }

    /// <summary>
    /// Approves whatever the mind is asking about until it stops asking. There is never more than one card
    /// at a time: the mind sees what the user decided about one operation before it proposes the next.
    /// </summary>
    private static void ApproveEach(Scenario s, int limit = 8)
    {
        for (var i = 0; i < limit && s.Snap.PendingProposals.Any(); i++) s.Approve();
        if (s.Snap.PendingProposals.Any()) throw s.Fail($"Still {s.Snap.PendingProposals.Count()} proposal(s) pending after {limit} approval(s)");
    }

    // ----------------------------------------------------------------------------------------
    // The mind this workflow is driven by
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// What the mind does with this workflow. A move reads the projects (their ids are what a proposal
    /// names), searches for the notes on the topic, proposes the destination when no project answers to it,
    /// and then proposes one move per note that is not already there. A question is answered from the notes
    /// it opens. Scripted so the workflow is deterministic; every consequence after it is the real path.
    /// </summary>
    private static ScriptedMind Working(string topic = Topic) => new ScriptedMind().Always(request =>
        Asked(request).StartsWith("move", StringComparison.OrdinalIgnoreCase) ? Moving(request, topic) : Answering(request));

    private static MindStep Moving(MindRequest request, string topic)
    {
        var projects = Projects(request);
        if (projects is null)
            return MindStep.Of(Tool("list_projects"), "Looking at your projects", Read(0.3, MindRead.NeedLocalNotes));
        if (request.Transcript.OfType<ToolObserved>().All(t => t.Tool != "search"))
            return MindStep.Of(Tool("search", ("query", topic), ("limit", "25")), $"Searching the notes for \"{topic}\"");

        var slug = Slug.From(Destination);
        var found = Hits(request).Where(h => h.Kind == SearchIndex.NoteKind).ToList();
        var elsewhere = found.Where(h => h.ProjectSlug != slug).ToList();
        if (elsewhere.Count == 0)
            return MindStep.Of(Say(found.Count == 0
                ? $"No note mentions the {topic}, so there is nothing to move and no reason to create '{Destination}'."
                : $"Every note about the {topic} is already in '{Destination}'. Nothing to move."), "Nothing to move");

        var decided = request.Transcript.OfType<PolicyObserved>().ToList();
        var destination = projects.GetValueOrDefault(slug) ?? Created(request);
        if (destination is null && decided.All(p => p.Action != Actions.CreateProject))
            return MindStep.Of(Propose(Actions.CreateProject, $"No active project answers to '{Destination}' and the notes about the {topic} need somewhere to go.",
                    ("name", Destination), ("slug", slug)),
                $"'{Destination}' does not exist: proposing it before anything moves");

        var moves = decided.Count(p => p.Action == Actions.MoveNote);
        if (destination is null && moves > 0)
            return MindStep.Of(Say($"'{Destination}' was not created, so the notes about the {topic} stay where they are."), "Nothing moved");
        if (moves < elsewhere.Count)
        {
            var note = elsewhere[moves];
            return MindStep.Of(Propose(Actions.MoveNote, $"The note is about the {topic}.",
                    ("projectId", projects[note.ProjectSlug!]), ("noteId", note.Id), ("toProject", destination ?? Destination)),
                $"Moving note {note.Id[^8..]} into '{Destination}'");
        }
        var moved = request.Transcript.OfType<ExecutionObserved>().Count(e => e.Action == Actions.MoveNote && e.Ok);
        return MindStep.Of(Say($"Moved {moved} note(s) about the {topic} into '{Destination}'."), "Done");
    }

    /// <summary>A question about the record: search, open every note the search listed, answer with where each one lives and what it says.</summary>
    private static MindStep Answering(MindRequest request)
    {
        var calls = request.Transcript.OfType<ToolObserved>().ToList();
        if (calls.Count == 0)
            return MindStep.Of(Tool("search", ("query", Asked(request)), ("limit", "10")), "Looking for what is stored about it", Read(0.3, MindRead.NeedLocalNotes));
        var notes = Hits(request).Where(h => h.Kind == SearchIndex.NoteKind).ToList();
        if (calls.Count - 1 < notes.Count)
            return MindStep.Of(Tool("read_note", ("projectId", notes[calls.Count - 1].ProjectSlug!), ("noteId", notes[calls.Count - 1].Id)), "Reading the record");
        var bodies = calls.Where(t => t.Tool == "read_note" && t.Ok && t.Data is not null).Select(t => Body(t.Data!)).ToList();
        return MindStep.Of(Say(string.Join("\n", notes.Zip(bodies, (note, body) => $"{note.ProjectSlug}/{note.Type}: {body}"))), "Answering from the record");
    }

    /// <summary>The words the task was given.</summary>
    private static string Asked(MindRequest request) => request.Transcript.OfType<InputObserved>().First().Text;

    /// <summary>Slug to id for every project the mind has listed, or null before it has listed them.</summary>
    private static Dictionary<string, string>? Projects(MindRequest request)
    {
        var listed = request.Transcript.OfType<ToolObserved>().FirstOrDefault(t => t.Tool == "list_projects" && t.Data is not null);
        if (listed is null) return null;
        using var projects = JsonDocument.Parse(listed.Data!);
        var bySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in projects.RootElement.EnumerateArray()) bySlug[p.GetProperty("slug").GetString()!] = p.GetProperty("id").GetString()!;
        return bySlug;
    }

    /// <summary>The destination this task created, when it did: the id its moves must name.</summary>
    private static string? Created(MindRequest request) => request.Transcript.OfType<ExecutionObserved>()
        .FirstOrDefault(e => e.Action == Actions.CreateProject && e.Ok)?.Outputs.GetValueOrDefault("projectId");

    /// <summary>One thing the search listed. A note carries the slug of the project it lives in; a draft carries none.</summary>
    private sealed record Hit(string Kind, string Id, string? ProjectSlug, string Type);

    private static List<Hit> Hits(MindRequest request)
    {
        var search = request.Transcript.OfType<ToolObserved>().FirstOrDefault(t => t.Tool == "search" && t.Data is not null);
        if (search is null) return [];
        using var hits = JsonDocument.Parse(search.Data!);
        return hits.RootElement.EnumerateArray()
            .Select(h => new Hit(h.GetProperty("kind").GetString()!, h.GetProperty("id").GetString()!,
                h.GetProperty("projectSlug").GetString(), h.GetProperty("type").GetString()!))
            .ToList();
    }

    private static string Body(string data)
    {
        using var note = JsonDocument.Parse(data);
        return note.RootElement.GetProperty("body").GetString()!;
    }

    // ----------------------------------------------------------------------------------------
    // The workflow
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void MovingNotesIntoAProjectThatDoesNotExistProposesThatProjectBeforeAnythingMoves()
    {
        var mind = Working();
        using var s = Seeded(_tmp, mind);
        Assert.Equal(4, NotesOf(s, "home").Count);

        s.Command("move the backyard notes into Garden")
            .ExpectState(RelayState.AwaitingApproval)
            .ExpectProposal(Actions.CreateProject, "pending")
            .ExpectEvent(EventTypes.MindStepped, 3)             // the projects, the search, the proposal
            .ExpectEvent(EventTypes.LoopWaiting);

        var create = Assert.Single(s.Snap.PendingProposals);
        Assert.Equal("garden", create.Target["slug"]);
        Assert.Equal(mind.Name, s.Response.Producer);
        // Nothing can be offered towards a project that does not exist, and nothing has run.
        Assert.DoesNotContain(s.Response.Proposals, p => p.Action == Actions.MoveNote);
        s.ExpectProject("garden", exists: false).ExpectNoEvent(EventTypes.NoteMoved);
    }

    [Fact]
    public void ApprovingEachProposalCreatesTheProjectFirstAndThenMovesEveryNoteWithItsHistory()
    {
        var mind = Working();
        using var s = Seeded(_tmp, mind);
        var before = NotesOf(s, "home").Where(n => n.Body.Contains("backyard")).Select(n => n.Id).OrderBy(x => x).ToList();

        s.Command("move the backyard notes into Garden");
        ApproveEach(s);
        s.ExpectState(RelayState.Completed).ExpectOutcome("executed")
            .ExpectProject("garden").ExpectEvent(EventTypes.ProjectCreated).ExpectEvent(EventTypes.NoteMoved, atLeast: 3);

        Assert.Equal(4, s.Response.Proposals.Count);
        Assert.All(s.Response.Proposals, p => Assert.Equal("executed", p.Status));
        Assert.All(s.Response.Proposals.Where(p => p.Action == Actions.MoveNote), p => Assert.Equal("garden", p.Target["toProjectSlug"]));
        var garden = NotesOf(s, "garden").Select(n => n.Id).OrderBy(x => x).ToList();
        Assert.Equal(before, garden);                                              // the same notes, same ids, now in Garden
        var home = NotesOf(s, "home");
        Assert.Single(home);
        Assert.Contains("kitchen", home[0].Body);                                  // the unrelated note was not swept up

        // Order in the ledger: the project exists before any note moves.
        var records = s.H.Records().ToList();
        var created = records.FindIndex(r => r.Type == EventTypes.ProjectCreated && r.DataString("slug") == "garden");
        var firstMove = records.FindIndex(r => r.Type == EventTypes.NoteMoved);
        Assert.True(created >= 0 && created < firstMove, "the project must be created before the first move");

        // The source keeps a pointer to where each note went, so the move is traceable from either side.
        var homeRoot = s.H.Registry.FindActive("home")!.RootPath;
        foreach (var id in before) Assert.True(File.Exists(Path.Combine(ProjectLayout.VersionsDirectory(homeRoot), id, "moved-to.txt")), $"no moved-to pointer for {id}");

        // The moved notes are searchable under Garden straight away.
        s.Command("what did I say about the backyard fence").ExpectAnswerContains("garden/");
    }

    [Fact]
    public void RefusingTheProjectLeavesTheNotesWhereTheyAreAndPolicyDeniesTheMoveThatNeededIt()
    {
        var mind = Working();
        using var s = Seeded(_tmp, mind);
        var movesBefore = s.H.Count(EventTypes.NoteMoved);

        s.Command("move the backyard notes into Garden")
            .Reject(Actions.CreateProject, "not a new project")
            .ExpectState(RelayState.Completed).ExpectOutcome("rejected");

        // The move the mind tried anyway names a project that does not exist, and the registry is the judge of that.
        var move = Assert.Single(s.Response.Proposals, p => p.Action == Actions.MoveNote);
        Assert.Equal("denied", move.Status);
        Assert.Contains(move.Reasons, r => r.Contains($"No active project matches '{Destination}'"));
        s.ExpectProject("garden", exists: false).ExpectEvent(EventTypes.ApprovalRejected);
        Assert.Equal(movesBefore, s.H.Count(EventTypes.NoteMoved));
        Assert.Equal(4, NotesOf(s, "home").Count);

        var rejected = s.H.Last(EventTypes.ApprovalRejected)!;
        Assert.Equal(Actions.CreateProject, rejected.DataString("action"));
        Assert.Equal("not a new project", rejected.DataString("reason"));
    }

    [Fact]
    public void PartialApprovalMovesOnlyWhatWasApproved()
    {
        var mind = Working();
        using var s = Seeded(_tmp, mind);

        s.Command("move the backyard notes into Garden")
            .Approve(Actions.CreateProject).ExpectProject("garden")
            .Approve(Actions.MoveNote)
            .Reject(Actions.MoveNote, "this one stays")
            .Approve(Actions.MoveNote)
            .ExpectState(RelayState.Completed).ExpectOutcome("executed");

        Assert.Equal(2, NotesOf(s, "garden").Count);
        Assert.Equal(2, NotesOf(s, "home").Count);                                 // kitchen + the one that stayed
        var stayed = Assert.Single(s.Response.Proposals, p => p.Status == "rejected");
        Assert.Contains(NotesOf(s, "home"), n => n.Id == stayed.Target["noteId"]);
        Assert.Equal(3, s.Response.Proposals.Count(p => p.Status == "executed"));   // the project and the two moves
    }

    [Fact]
    public void RenamingTheNewProjectInTheEditSendsTheMovesToTheNameThatWasCreated()
    {
        var mind = Working();
        using var s = Seeded(_tmp, mind)
            .Command("move the backyard notes into Garden")
            .Edit(Actions.CreateProject, ("name", "Yard"), ("slug", "yard"));
        Assert.Equal("Yard", Assert.Single(s.Snap.PendingProposals).Target["name"]);

        ApproveEach(s);
        s.ExpectState(RelayState.Completed).ExpectOutcome("executed")
            .ExpectProject("yard").ExpectProject("garden", exists: false);
        var moves = s.Response.Proposals.Where(p => p.Action == Actions.MoveNote).ToList();
        Assert.Equal(3, moves.Count);
        Assert.All(moves, m => { Assert.Equal("executed", m.Status); Assert.Equal("yard", m.Target["toProjectSlug"]); });
        Assert.Equal(3, NotesOf(s, "yard").Count);
    }

    [Fact]
    public void WhenTheDestinationExistsThereIsNoProjectToProposeAndNotesAlreadyThereAreLeftAlone()
    {
        var mind = Working();
        using var s = Seeded(_tmp, mind).Project("Garden").Command("move the backyard notes into Garden");
        var garden = s.H.Registry.FindActive("garden")!;

        ApproveEach(s);
        s.ExpectState(RelayState.Completed).ExpectOutcome("executed");
        Assert.DoesNotContain(s.Response.Proposals, p => p.Action == Actions.CreateProject);
        Assert.Equal(3, s.Response.Proposals.Count(p => p.Action == Actions.MoveNote));
        Assert.All(s.Response.Proposals, p => Assert.Equal(garden.Id, p.Target["toProjectId"]));
        Assert.Equal(3, NotesOf(s, "garden").Count);

        // Asking again finds every backyard note already in Garden: an answer, not a proposal.
        s.Command("move the backyard notes into Garden")
            .ExpectState(RelayState.Completed).ExpectNoProposals().ExpectOutcome("answered");
        Assert.All(Hits(mind.Requests[^1]).Where(h => h.Kind == SearchIndex.NoteKind), h => Assert.Equal("garden", h.ProjectSlug));
    }

    [Fact]
    public void ATopicNobodyWroteAboutMovesNothingAndCreatesNothing()
    {
        var mind = Working("greenhouse");
        using var s = Seeded(_tmp, mind)
            .Command("move the greenhouse notes into Garden")
            .ExpectState(RelayState.Completed).ExpectNoProposals().ExpectOutcome("answered")
            .ExpectProject("garden", exists: false);
        Assert.DoesNotContain(Hits(mind.Requests[^1]), h => h.Kind == SearchIndex.NoteKind);
    }

    [Fact]
    public void AMoveTowardsAPlannedProjectIsDeniedAtRunTimeIfTheProjectIsStillMissing()
    {
        // Policy accepts a planned destination only while deciding; at execution the real registry is the judge.
        using var s = Seeded(_tmp);
        var home = s.H.Registry.FindActive("home")!;
        var note = NotesOf(s, "home").First();
        var target = new Dictionary<string, string> { ["projectId"] = home.Id, ["noteId"] = note.Id, ["toProject"] = Destination };
        PolicyWorld World(IReadOnlyList<string> planned) => new()
        {
            Registry = s.H.Registry, Roots = s.H.Roots, DataRoot = _tmp.Root, DraftNoteExists = _ => false,
            ProjectNoteExists = (projectId, noteId) => s.H.Registry.ById(projectId) is { } p && ProjectNoteStore.Find(p.RootPath, noteId) is not null,
            PlannedProjects = planned,
        };
        var proposal = new Proposal("01HZZZZZZZZZZZZZZZZZZZZZZ1", Actions.MoveNote, "test", target, ["01HZZZZZZZZZZZZZZZZZZZZZZ0"], [], Risks.ControlledWrite, true, Producers.Mind);

        var deciding = PolicyEngine.Decide(proposal, World(["garden"]));
        Assert.Equal(DecisionOutcome.NeedsApproval, deciding.Outcome);
        Assert.Equal("garden", deciding.NormalizedTarget["toProjectSlug"]);

        var running = PolicyEngine.Decide(proposal, World([]));
        Assert.Equal(DecisionOutcome.Deny, running.Outcome);
        Assert.Contains(running.Reasons, r => r.Contains($"No active project matches '{Destination}'"));
    }

    public void Dispose() => _tmp.Dispose();
}
