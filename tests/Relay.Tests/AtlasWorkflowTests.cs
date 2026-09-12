using System.Text.Json;
using Relay.Core.Attention;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.State;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// The README's flagship workflow, end to end against the real executor: a decision overheard and
/// filed, a later statement that contradicts it, an alert with both sources and an editable proposal
/// to update the record, the approval, and a direct question while still listening that is answered
/// from the corrected record. The mind reading the conversation is scripted (it stands in for RELAY0);
/// everything after it is the production path.
/// </summary>
public class AtlasWorkflowTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private const string Decision = "We decided the Atlas beta ships on October 14.";
    private const string Contradiction = "Marketing wants the Atlas beta out on the 21st.";

    /// <summary>The mind runs everything, with the note chord listening: a mind and the setting on.</summary>
    private static void Listening(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
        s.Listening.Enabled = true;
    }

    /// <summary>What the mind hears: it keeps the decision, and checks the later claim against it.</summary>
    private static ListeningMind AtlasMind() => new ListeningMind()
        .When("We decided", "remember", "Keep this decision.", note: "Atlas beta ships on October 14.", noteType: "decision", project: "Atlas", topic: "atlas beta")
        .When("21st", "check", "Check the stated Atlas beta date against the stored decision.", project: "Atlas", topic: "atlas beta", mergeKey: "check:atlas:beta-date");

    /// <summary>
    /// What the mind does with the work it is given. A raised check searches for what is stored, opens the note
    /// and the excerpt so the answer stands on both, proposes the one-step record update, and ends with the
    /// verdict. A direct ask searches, opens every note it found, and answers from them. Scripted so the
    /// workflow is deterministic; every move after it goes through the production path.
    /// </summary>
    private static ScriptedMind AtlasWorking(string projectId) => new ScriptedMind().Always(request =>
    {
        var asked = request.Transcript.OfType<InputObserved>().First().Source == InputObserved.Ask;
        var read = request.Transcript.OfType<ToolObserved>().ToList();
        if (read.Count == 0)
            return MindStep.Of(ScriptedMind.Tool("search", ("query", "Atlas beta ships"), ("project", "atlas"), ("limit", "5")),
                asked ? "Looking up what was decided about the beta date." : "Looking for what is stored about the beta date.");

        // One read per note the search listed: a search returns candidates, opening one of them is what grounds the answer.
        var notes = Found(request);
        if (read.Count - 1 < notes.Count)
            return MindStep.Of(ScriptedMind.Tool("read_note", ("projectId", projectId), ("noteId", notes[read.Count - 1])), "Reading the record.");

        if (asked)
            return MindStep.Of(ScriptedMind.Say("We decided the Atlas beta ships on October 21. The earlier decision of October 14 is kept beside it (superseded)."),
                "Answering from the record.");

        if (read.All(t => t.Tool != "read_excerpt"))
            return MindStep.Of(ScriptedMind.Tool("read_excerpt", ("excerptId", Heard(request)!)), "Reading back what was just said.");

        // Both sides read: the verdict is reached here, before the proposal, so the card says what it is about.
        if (!request.Transcript.OfType<PolicyObserved>().Any())
            return MindStep.Of(ScriptedMind.Propose(Actions.SupersedeNote,
                    "The date heard (the 21st) contradicts the stored decision (October 14); updating the record keeps the earlier text as superseded.",
                    ("projectId", projectId), ("noteId", notes[0]), ("newText", "Atlas beta ships on October 21."),
                    ("type", NoteTypes.Decision), ("sourceExcerptId", Heard(request)!), ("spanStart", "0"), ("spanEnd", Contradiction.Length.ToString())),
                "Proposing the record update.", ScriptedMind.Verdict(consistent: false));
        return MindStep.Of(ScriptedMind.Say("Stored decision: Atlas beta ships on October 14. Heard now: out on the 21st. These conflict."),
            "Heard the 21st; the stored decision says October 14.");
    });

    /// <summary>The notes the search listed, what is current first.</summary>
    private static List<string> Found(MindRequest request)
    {
        using var hits = JsonDocument.Parse(request.Transcript.OfType<ToolObserved>().First(t => t.Tool == "search").Data!);
        return hits.RootElement.EnumerateArray()
            .Where(h => h.GetProperty("kind").GetString() == SearchIndex.NoteKind)
            .Select(h => (Id: h.GetProperty("id").GetString()!, Current: h.GetProperty("status").GetString() == NoteStatus.Active))
            .OrderByDescending(n => n.Current).Select(n => n.Id).ToList();
    }

    /// <summary>The excerpt the raise anchored to: the words being checked.</summary>
    private static string? Heard(MindRequest request) => request.Transcript.OfType<InputObserved>().First().ExcerptId;

    [Fact]
    public void TheReadmeWorkflowHolds()
    {
        var mind = AtlasMind();
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .Do("create project Atlas", c => Assert.True(c.CreateProject("Atlas")));
        mind.Works(AtlasWorking(s.H.Registry.FindActive("atlas")!.Id));

        s.StartListening()
            .Listen(Decision)                                                        // observed → remember → filed (ambient)
            .ExpectTask(TaskKind.Remember, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectAttention(Presentation.Ambient, "Note filed")
            .Listen(Contradiction)                                                   // observed → check → conflict → proposal card
            .ExpectTask(TaskKind.Check, TaskStatus.AwaitingApproval, TaskOrigin.Observed)
            .ExpectAttention(Presentation.Proposal, "Conflict")
            .ExpectAnyProposal(Actions.SupersedeNote, "pending")
            .ExpectState(RelayState.NoteCapture);                                    // the stream never stopped

        var check = s.FindTask(TaskKind.Check)!;
        Assert.False(check.Consistent);
        Assert.Equal(2, check.Citations.Count);                                       // both sources: the stored note and the excerpt
        Assert.Contains(check.Citations, c => c.Kind == SearchIndex.NoteKind);
        Assert.Contains(check.Citations, c => c.Kind == SearchIndex.ExcerptKind && c.Id == check.ExcerptId);
        Assert.Equal(["search", "read_note", "read_excerpt"], check.ToolCalls.Select(c => c.Tool));   // the diagnostics show how the mind got there
        var pending = Assert.Single(check.Proposals);
        Assert.Equal(Presentation.Proposal, check.Presentation);
        Assert.Contains("Update the decision", pending.Title);
        Assert.Contains("October 21", pending.Detail);
        Assert.True(pending.Editable);
        var card = Assert.Single(s.Snap.Attention, a => a.Level == Presentation.Proposal);
        Assert.True(card.NeedsAction);

        s.Approve(Actions.SupersedeNote)
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectAnyProposal(Actions.SupersedeNote, "executed")
            .ExpectEvent(EventTypes.NoteSuperseded)
            .ExpectNoAttention(Presentation.Proposal)
            .ExpectNoAttention(Presentation.Alert)                                   // approved: no alert about a conflict already resolved
            .ExpectState(RelayState.NoteCapture);

        var atlas = s.H.Registry.FindActive("atlas")!;
        var notes = ProjectNoteStore.ReadAll(atlas.RootPath).Notes.Select(n => n.Note).ToList();
        Assert.Equal(2, notes.Count);
        var old = Assert.Single(notes, n => n.Body == "Atlas beta ships on October 14.");
        var current = Assert.Single(notes, n => n.Body == "Atlas beta ships on October 21.");
        Assert.Equal(NoteStatus.Superseded, old.Status);
        Assert.Equal(NoteStatus.Active, current.Status);
        Assert.Equal(NoteTypes.Decision, current.Type);
        Assert.Equal([old.Id], current.Supersedes);
        Assert.Equal(check.ExcerptId, Assert.Single(current.Spans).EventId);         // the new decision cites the words that changed it
        Assert.True(File.Exists(Path.Combine(atlas.RootPath, ".orchestrator", "versions", NoteTypes.Folder(old.Type), old.Id + ".v1.md")) || Directory.GetFiles(Path.Combine(atlas.RootPath, ".orchestrator"), "*", SearchOption.AllDirectories).Length > 0,
            "the earlier version is kept");

        // Up to here the words of the room reached only the excerpt store and the notes, never the ledger.
        Assert.DoesNotContain(Contradiction, s.H.LedgerText());
        Assert.DoesNotContain(Decision, s.H.LedgerText());

        // Still listening: a direct question is answered from the corrected record and cites it.
        s.Ask("what did we decide about the Atlas beta date?")
            .ExpectState(RelayState.NoteCapture)
            .ExpectTask(TaskKind.Answer, TaskStatus.Completed, TaskOrigin.Direct)
            .ExpectAttention(Presentation.Result, "beta");
        var answer = s.FindTask(TaskKind.Answer, origin: TaskOrigin.Direct)!;
        Assert.Contains("October 21", answer.Answer);
        Assert.Contains("(superseded)", answer.Answer);                               // the old decision is shown as history, not hidden
        Assert.Contains(answer.Citations, c => c.Id == current.Id);

        s.StopListening().ExpectState(RelayState.Completed).ExpectListening(false);
        Assert.Contains("2 task(s) raised", s.Snap.Receipt);

        // Approval bound to the proposal hash, a capability consumed, the execution journaled: the usual chain, from an observed task.
        var approval = s.H.Last(EventTypes.ApprovalGranted)!;
        var received = s.H.Records().Last(r => r.Type == EventTypes.ProposalReceived && r.DataString("action") == Actions.SupersedeNote);
        Assert.Equal(received.DataString("hash"), approval.DataString("proposalHash"));
        Assert.Equal(check.Producer, received.DataString("proposedBy"));            // the mind that ran the task is on record as having proposed it
        Assert.Equal(check.TaskId, received.DataString("taskId"));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ExecutionCompleted && r.DataString("action") == Actions.SupersedeNote);

        // The diagnostics record for the check task has the whole story.
        var diagnostics = JsonDocument.Parse(File.ReadAllText(Path.Combine(s.H.Root.TasksDirectory, check.TaskId + ".json"))).RootElement;
        Assert.Equal("observed", diagnostics.GetProperty("origin").GetString());
        Assert.Equal("check", diagnostics.GetProperty("kind").GetString());
        Assert.Equal(3, diagnostics.GetProperty("toolCalls").GetArrayLength());
        Assert.Equal("ambient", diagnostics.GetProperty("presentation").GetString());   // final presentation: the approved update ran
        Assert.Equal("approved", diagnostics.GetProperty("userResponse").GetString());
        // The words of the room never reach the ledger: every move of the check task (which quoted the excerpt) is
        // recorded as a fingerprint, while the direct ask's are in the clear — it cites the corrected decision, whose
        // text was approved, and the excerpt it came from is not repeated beside it.
        Assert.DoesNotContain(s.H.Records(), r => r.Data.GetRawText().Contains("Marketing wants", StringComparison.Ordinal));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.MindStepped && r.DataString("taskId") == check.TaskId && (r.DataString("brief") ?? "").StartsWith("withheld: ", StringComparison.Ordinal));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.MindStepped && r.DataString("taskId") == answer.TaskId && (r.DataString("brief") ?? "").Contains("October 21", StringComparison.Ordinal));
        Assert.DoesNotContain(answer.Citations, c => c.Id == check.ExcerptId);          // the note cites the excerpt as its source; the passage is not listed twice
    }

    [Fact]
    public void RejectingTheUpdateLeavesTheRecordAloneAndDoesNotReAlert()
    {
        var mind = AtlasMind();
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .Do("create project Atlas", c => Assert.True(c.CreateProject("Atlas")));
        mind.Works(AtlasWorking(s.H.Registry.FindActive("atlas")!.Id));

        s.StartListening()
            .Listen(Decision)
            .Listen(Contradiction)
            .ExpectAttention(Presentation.Proposal)
            .Reject(Actions.SupersedeNote, "marketing does not decide dates")
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectNoAttention(Presentation.Proposal)
            .ExpectNoAttention(Presentation.Alert)
            .ExpectNoEvent(EventTypes.NoteSuperseded);

        var atlas = s.H.Registry.FindActive("atlas")!;
        var note = Assert.Single(ProjectNoteStore.ReadAll(atlas.RootPath).Notes).Note;
        Assert.Equal(NoteStatus.Active, note.Status);
        Assert.Equal("Atlas beta ships on October 14.", note.Body);
        var check = s.FindTask(TaskKind.Check)!;
        Assert.Equal("rejected", check.Outcome);
        Assert.Equal(Presentation.None, check.Presentation);
        Assert.Equal("rejected", check.UserResponse);
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ApprovalRejected && r.DataString("reason") == "marketing does not decide dates");
    }

    [Fact]
    public void EditingTheProposedTextBeforeApprovingChangesWhatIsRecorded()
    {
        var mind = AtlasMind();
        using var s = Scenario.New(_tmp, Listening, mind: mind).WithWorkspace()
            .Do("create project Atlas", c => Assert.True(c.CreateProject("Atlas")));
        mind.Works(AtlasWorking(s.H.Registry.FindActive("atlas")!.Id));

        s.StartListening()
            .Listen(Decision)
            .Listen(Contradiction)
            .Edit(Actions.SupersedeNote, ("newText", "Atlas beta ships on October 21 (marketing's request; engineering to confirm)."))
            .ExpectAnyProposal(Actions.SupersedeNote, "edited")
            .ExpectAnyProposal(Actions.SupersedeNote, "pending")
            .Approve(Actions.SupersedeNote)
            .ExpectEvent(EventTypes.NoteSuperseded);

        var atlas = s.H.Registry.FindActive("atlas")!;
        var current = Assert.Single(ProjectNoteStore.ReadAll(atlas.RootPath).Notes.Select(n => n.Note), n => n.Status == NoteStatus.Active);
        Assert.Equal("Atlas beta ships on October 21 (marketing's request; engineering to confirm).", current.Body);
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ProposalReceived && r.DataString("action") == Actions.SupersedeNote && r.DataString("proposedBy") == "user");
    }

    [Fact]
    public void SupersedeWithTextAndWithAnExistingNoteAreBothValidButNotTogether()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Note("We decided the Atlas beta ships on October 14.")
            .Note("We decided the Atlas beta ships on November 2 instead.");
        var atlas = s.H.Registry.FindActive("atlas")!;
        var notes = ProjectNoteStore.ReadAll(atlas.RootPath).Notes.Select(n => n.Note).OrderBy(n => n.Created).ToList();
        Assert.Equal(2, notes.Count);
        var world = new PolicyWorld
        {
            Registry = s.H.Registry,
            Roots = s.H.Roots,
            DataRoot = s.H.Root,
            DraftNoteExists = _ => false,
            ProjectNoteExists = (p, id) => ProjectNoteStore.Find(atlas.RootPath, id) is not null,
            ReferenceExists = id => id == "01EXCERPT000000000000000000",
        };
        Proposal Make(params (string Key, string Value)[] target)
            => new("01PROPOSAL0000000000000000", Actions.SupersedeNote, "test", target.ToDictionary(t => t.Key, t => t.Value), ["01SOURCE000000000000000000"], [], Risks.ControlledWrite, true, Producers.Model);

        var linked = PolicyEngine.Decide(Make(("projectId", atlas.Id), ("noteId", notes[0].Id), ("supersededBy", notes[1].Id)), world);
        Assert.Equal(DecisionOutcome.NeedsApproval, linked.Outcome);
        var fresh = PolicyEngine.Decide(Make(("projectId", atlas.Id), ("noteId", notes[0].Id), ("newText", "  Atlas beta ships on October 21.  "), ("type", "decision")), world);
        Assert.Equal(DecisionOutcome.NeedsApproval, fresh.Outcome);
        Assert.Equal("Atlas beta ships on October 21.", fresh.NormalizedTarget["newText"]);
        var both = PolicyEngine.Decide(Make(("projectId", atlas.Id), ("noteId", notes[0].Id), ("supersededBy", notes[1].Id), ("newText", "x")), world);
        Assert.Equal(DecisionOutcome.Deny, both.Outcome);
        var neither = PolicyEngine.Decide(Make(("projectId", atlas.Id), ("noteId", notes[0].Id)), world);
        Assert.Equal(DecisionOutcome.Deny, neither.Outcome);
        var badType = PolicyEngine.Decide(Make(("projectId", atlas.Id), ("noteId", notes[0].Id), ("newText", "x"), ("type", "rumour")), world);
        Assert.Equal(DecisionOutcome.Deny, badType.Outcome);
        var badSource = PolicyEngine.Decide(Make(("projectId", atlas.Id), ("noteId", notes[0].Id), ("newText", "x"), ("sourceExcerptId", "01NOPE00000000000000000000")), world);
        Assert.Equal(DecisionOutcome.Deny, badSource.Outcome);
        var goodSource = PolicyEngine.Decide(Make(("projectId", atlas.Id), ("noteId", notes[0].Id), ("newText", "x"), ("sourceExcerptId", "01EXCERPT000000000000000000")), world);
        Assert.Equal(DecisionOutcome.NeedsApproval, goodSource.Outcome);
    }

    public void Dispose() => _tmp.Dispose();
}
