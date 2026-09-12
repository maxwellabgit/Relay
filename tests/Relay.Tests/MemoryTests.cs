using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Memory;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Search;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Tests.Support;
using static Relay.Core.Mind.ScriptedMind;

namespace Relay.Tests;

/// <summary>Phase 6: extraction with exact spans, confidence routing, disputes and supersession, recall over all of it.</summary>
public class MemoryTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    /// <summary>
    /// Filing needs nothing interpreted: the note chord cuts, routes and checks for conflicts on its own,
    /// which is why most of what follows holds without a mind at all. The mode is named here because
    /// recall is the mind's, and because a Relay configured for a mind it does not have says so in Review.
    /// </summary>
    private static void Mind(RelaySettings s)
    {
        s.Orchestrator.Mode = OrchestratorSettings.Mind;
        s.Model.Enabled = true;
    }

    /// <summary>
    /// What the mind does with a question about the record: search for what is stored, then open every note
    /// the search listed — a search returns candidates, opening one of them is what grounds an answer — and
    /// answer from what it read. A note the record itself no longer calls current is answered with that
    /// mark, so a conclusion that was replaced is never presented as the standing one.
    /// </summary>
    private static ScriptedMind Recalling() => new ScriptedMind().Always(request =>
    {
        var calls = request.Transcript.OfType<ToolObserved>().ToList();
        if (calls.Count == 0)
            return MindStep.Of(Tool("search", ("query", Asked(request)), ("limit", "10")), "Looking for what is stored about it", Read(0.3, MindRead.NeedLocalNotes));

        // One read per note the search listed; drafts in staging are not notes anything can open.
        var notes = Hits(request).Where(h => h.Kind == SearchIndex.NoteKind).ToList();
        if (calls.Count - 1 < notes.Count)
            return MindStep.Of(Tool("read_note", ("projectId", notes[calls.Count - 1].ProjectSlug!), ("noteId", notes[calls.Count - 1].Id)), "Reading the record");
        return MindStep.Of(Say(Grounded(request)), "Answering from the record");
    });

    /// <summary>The words the task was given.</summary>
    private static string Asked(MindRequest request) => request.Transcript.OfType<InputObserved>().First().Text;

    /// <summary>One thing the search listed. A note carries the slug of the project it lives in; a draft carries none.</summary>
    private sealed record Hit(string Kind, string Id, string? ProjectSlug, string Excerpt);

    /// <summary>What the search this task made found, in the order it ranked them.</summary>
    private static List<Hit> Hits(MindRequest request)
    {
        var search = request.Transcript.OfType<ToolObserved>().FirstOrDefault(t => t.Tool == "search" && t.Data is not null);
        if (search is null) return [];
        using var hits = JsonDocument.Parse(search.Data!);
        return hits.RootElement.EnumerateArray()
            .Select(h => new Hit(h.GetProperty("kind").GetString()!, h.GetProperty("id").GetString()!,
                h.GetProperty("projectSlug").GetString(), h.GetProperty("excerpt").GetString()!))
            .ToList();
    }

    private static (string Id, string Type, string Status, string Body) Opened(string data)
    {
        using var note = JsonDocument.Parse(data);
        var n = note.RootElement;
        return (n.GetProperty("id").GetString()!, n.GetProperty("type").GetString()!, n.GetProperty("status").GetString()!, n.GetProperty("body").GetString()!);
    }

    /// <summary>
    /// The answer: one line per note the mind opened, in the order the search ranked them, plus a line for
    /// each draft still waiting in staging. Nothing the mind did not look at is quoted.
    /// </summary>
    private static string Grounded(MindRequest request)
    {
        var opened = request.Transcript.OfType<ToolObserved>()
            .Where(t => t.Tool == "read_note" && t.Ok && t.Data is not null)
            .Select(t => Opened(t.Data!))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var hit in Hits(request))
        {
            if (hit.Kind == SearchIndex.NoteKind && opened.TryGetValue(hit.Id, out var note))
                lines.Add($"{hit.ProjectSlug}/{note.Type}: {note.Body}" + (note.Status == NoteStatus.Active ? "" : $" ({note.Status})"));
            else if (hit.Kind == SearchIndex.DraftKind) lines.Add("in staging: " + hit.Excerpt);
        }
        return lines.Count == 0 ? "Nothing stored mentions that." : string.Join("\n", lines);
    }

    /// <summary>The README's scenario, verbatim, so the documentation cannot drift from the behaviour.</summary>
    [Fact]
    public void ReadmeScenarioHolds()
    {
        using var s = Scenario.New(_tmp, Mind, mind: Recalling()).WithWorkspace()
            .Project("Atlas")
            .Note("We decided the Atlas beta ships on October 14. Need to email the Atlas pilot customers before then.")
            .ExpectEvent(EventTypes.NoteRouted, atLeast: 2)
            .Command("what did I say about the beta?")
            .ExpectOutcome("answered").ExpectAnswerContains("October 14");
    }

    // ----------------------------------------------------------------------------------------
    // Pure units
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ExtractorCutsSentencesAndKeepsExactSpans()
    {
        const string text = "We decided to ship the Atlas beta on October 14. Need to email the pilot customers before then. What if we bundled the onboarding video with the invite?";
        var notes = NoteExtractor.Extract(text);

        Assert.Equal(3, notes.Count);
        Assert.Equal(NoteTypes.Decision, notes[0].Type);
        Assert.Equal(NoteTypes.Task, notes[1].Type);
        Assert.Equal(NoteTypes.Question, notes[2].Type);
        foreach (var n in notes) Assert.Equal(n.Text, text[n.Start..n.End]); // spans are exact, text is never rewritten
        Assert.Equal(0, notes[0].Start);
        Assert.Equal(text.Length, notes[2].End);
    }

    [Fact]
    public void ExtractorMergesFragmentsAndSplitsParagraphs()
    {
        const string text = "Ok. So the plan for the garden this spring is raised beds along the south fence.\n\nSee https://example.org/raised-beds for the dimensions we liked.";
        var notes = NoteExtractor.Extract(text);

        Assert.Equal(2, notes.Count);
        Assert.StartsWith("Ok. So the plan", notes[0].Text); // the 3-char fragment merged into its successor
        Assert.Equal(NoteTypes.Reference, notes[1].Type);
        Assert.Equal(notes[1].Text, text[notes[1].Start..notes[1].End]);
    }

    [Fact]
    public void RouterIsConfidentOnExplicitMentionAndAmbiguousBetweenTwoMentions()
    {
        var atlas = new ProjectRecord { Id = "A", Slug = "atlas", Name = "Atlas", RootPath = Path.Combine(_tmp.Root.Path, "none-a"), CreatedAt = DateTimeOffset.UnixEpoch };
        var garden = new ProjectRecord { Id = "G", Slug = "garden", Name = "Garden", RootPath = Path.Combine(_tmp.Root.Path, "none-g"), CreatedAt = DateTimeOffset.UnixEpoch };
        var profiles = new[] { ProjectProfile.Build(atlas), ProjectProfile.Build(garden) };

        var single = NoteRouter.Route("The Atlas launch moves to October.", profiles);
        Assert.Equal("atlas", single.Best!.Slug);
        Assert.True(single.Confidence >= NoteRouter.MentionWeight);

        var both = NoteRouter.Route("Atlas and Garden both need budgets.", profiles);
        Assert.True(both.Confidence <= NoteRouter.AmbiguityCap, both.Summary);
        Assert.Contains(both.Best!.Reasons, r => r.StartsWith("ambiguous with"));

        var none = NoteRouter.Route("Buy milk on the way home.", profiles);
        Assert.Null(none.Best);
    }

    [Fact]
    public void RouterUsesVocabularyOfExistingNotesAsWeakEvidence()
    {
        var root = Path.Combine(_tmp.Root.Path, "vocab-project");
        var record = new ProjectRecord { Id = "V", Slug = "vocab", Name = "Vocab", RootPath = root, CreatedAt = DateTimeOffset.UnixEpoch };
        ProjectLayout.Create(record, DateTimeOffset.UnixEpoch);
        ProjectNoteStore.WriteNew(root, new NoteDocument { Id = "N1", ProjectId = "V", Type = NoteTypes.Fact, Created = DateTimeOffset.UnixEpoch, Body = "The greenhouse thermostat and irrigation timer share one circuit." });
        var profile = ProjectProfile.Build(record);

        var r = NoteRouter.Route("The irrigation timer tripped the greenhouse circuit again.", [profile]);
        Assert.Equal("vocab", r.Best!.Slug);
        Assert.Contains(r.Best.Reasons, x => x.Contains("already appear in its notes"));
        Assert.True(r.Confidence < NoteRouter.MentionWeight); // weak evidence alone never reaches the automatic tier by default
        Assert.True(r.Confidence > 0);
    }

    [Fact]
    public void DisputeDetectorFlagsSimilarDecisionsThatSayDifferentThingsOnly()
    {
        var existing = new NoteDocument { Id = "OLD", ProjectId = "P", Type = NoteTypes.Decision, Created = DateTimeOffset.UnixEpoch, Body = "We decided the Atlas beta ships on October 14." };
        var findings = DisputeDetector.Find(NoteTypes.Decision, "We decided the Atlas beta ships on November 2.", [existing]);
        Assert.Single(findings);
        Assert.Equal("OLD", findings[0].Existing.Id);

        Assert.Empty(DisputeDetector.Find(NoteTypes.Fact, "We decided the Atlas beta ships on November 2.", [existing]));          // only decisions dispute
        Assert.Empty(DisputeDetector.Find(NoteTypes.Decision, "We decided the Atlas beta ships on October 14.", [existing]));    // identical text is a repeat, not a dispute
        Assert.Empty(DisputeDetector.Find(NoteTypes.Decision, "We decided to repaint the garden shed green.", [existing]));      // unrelated words
        existing.Status = NoteStatus.Superseded;
        Assert.Empty(DisputeDetector.Find(NoteTypes.Decision, "We decided the Atlas beta ships on November 2.", [existing]));    // superseded notes no longer dispute
    }

    // ----------------------------------------------------------------------------------------
    // Note mode end to end
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void NoteModeFilesConfidentNotesAutomaticallyWithSpansBackToTheCapture()
    {
        using var s = Scenario.New(_tmp, Mind).WithWorkspace()
            .Project("Atlas")
            .Note("We decided the Atlas beta ships on October 14. Need to email the Atlas pilot customers before then.")
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.NoteExtracted)
            .ExpectEvent(EventTypes.NoteDraftCreated, atLeast: 2)
            .ExpectEvent(EventTypes.NoteRouted, atLeast: 2)
            .ExpectEvent(EventTypes.NoteWritten, atLeast: 2);
        Assert.Equal(1, s.H.Count(EventTypes.ApprovalGranted)); // only the project creation asked; filing into an existing project is Tier A

        Assert.Contains("2 filed", s.Snap.Receipt);
        var project = s.H.Registry.FindActive("atlas")!;
        var (notes, problems) = ProjectNoteStore.ReadAll(project.RootPath);
        Assert.Empty(problems);
        Assert.Equal(2, notes.Count);
        var decision = notes.Single(n => n.Note.Type == NoteTypes.Decision).Note;
        var task = notes.Single(n => n.Note.Type == NoteTypes.Task).Note;
        Assert.Equal(NoteStatus.Active, decision.Status);
        Assert.True(decision.Confidence >= 0.75);

        var capture = s.H.Records().Last(r => r.Type == EventTypes.CaptureCommitted && r.DataString("mode") == "note");
        foreach (var n in new[] { decision, task })
        {
            var span = Assert.Single(n.Spans);
            Assert.Equal(capture.Id, span.EventId);
            Assert.Equal(n.Body, capture.DataString("text")![span.Start..span.End]); // provenance: the note is exactly these words of this capture
        }
        Assert.Empty(s.H.Notes.Unrouted());

        // The proposal → policy → executor path was used even though nobody approved anything.
        var decided = s.H.Records().Where(r => r.Type == EventTypes.ProposalDecided && r.DataString("action") == Actions.RouteNote).ToList();
        Assert.Equal(2, decided.Count);
        Assert.All(decided, r => Assert.Equal("Allow", r.DataString("outcome")));
    }

    [Fact]
    public void NoteModeLeavesUnmentionedNotesInTheInboxAndAttachesCandidatesToAmbiguousOnes()
    {
        // A mind is in place, so an empty Review here means routing raised nothing — not that Relay has none.
        var mind = new ScriptedMind();
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Project("Garden")
            .Note("Buy compost and a new rake at the hardware store this weekend.")
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.NoteRoutingDeferred)
            .ExpectNoEvent(EventTypes.NoteRouted);
        Assert.Contains("unrouted", s.Snap.Receipt);
        Assert.Single(s.H.Notes.Unrouted());
        var compost = Assert.Single(s.Snap.Inbox);
        Assert.False(compost.HasSuggestions); // no evidence → nothing to ask, the note simply waits
        Assert.Empty(s.Snap.Review);          // routing is never a Review item

        s.Note("Atlas and Garden both need a budget line before the board meeting.")
            .ExpectState(RelayState.Completed)
            .ExpectNoEvent(EventTypes.NoteRouted);
        Assert.Contains("need your routing decision", s.Snap.Receipt);
        Assert.Empty(s.Snap.Review);
        var inbox = s.Snap.Inbox;
        Assert.Equal(2, inbox.Count);
        var budget = inbox.Single(i => i.HasSuggestions); // shown once, with the candidates attached
        Assert.Equal(inbox[0].NoteId, budget.NoteId);     // newest first
        Assert.Contains("ambiguous", budget.Summary);
        Assert.Equal(["atlas", "garden"], budget.Candidates.Select(c => c.Slug).Order().ToArray());

        // The user resolves it: the note is filed by the same controlled path with confidence 1 and leaves the inbox.
        var garden = s.H.Registry.FindActive("garden")!;
        s.Do("route to garden", c => Assert.True(c.RouteDraftNote(budget.NoteId, garden.Id)))
            .ExpectEvent(EventTypes.NoteRouted)
            .ExpectEvent(EventTypes.ApprovalGranted); // the click is recorded as the approval
        Assert.Equal([compost.NoteId], s.Snap.Inbox.Select(i => i.NoteId).ToArray()); // the compost note is still waiting, untouched
        Assert.Empty(s.C.PendingRoutingDecisions);
        var (notes, _) = ProjectNoteStore.ReadAll(garden.RootPath);
        Assert.Contains(notes, n => n.Note.Id == budget.NoteId && n.Note.Confidence == 1);

        // A note without suggestions is filed the same way from the inbox.
        var atlas = s.H.Registry.FindActive("atlas")!;
        s.Do("file compost under atlas", c => Assert.True(c.RouteDraftNote(compost.NoteId, atlas.Id)));
        Assert.Empty(s.Snap.Inbox);
        Assert.Empty(s.H.Notes.Unrouted());
        Assert.Empty(mind.Requests); // none of it asked anything of the mind
    }

    [Fact]
    public void InboxSuggestionsSurviveRestartAndCanBeDismissedKeepingTheNote()
    {
        using var s = Scenario.New(_tmp, Mind).WithWorkspace()
            .Project("Atlas")
            .Project("Garden")
            .Note("Atlas and Garden both need a budget line before the board meeting.");
        Assert.True(Assert.Single(s.Snap.Inbox).HasSuggestions);
        s.Restart();
        var item = Assert.Single(s.Snap.Inbox);
        Assert.True(item.HasSuggestions);

        s.Do("keep here", c => c.KeepUnrouted(item.NoteId));
        var kept = Assert.Single(s.Snap.Inbox);
        Assert.Equal(item.NoteId, kept.NoteId);
        Assert.False(kept.HasSuggestions); // the note stays; only the suggestions are retired
        Assert.Single(s.H.Notes.Unrouted());
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(_tmp.Root.ReviewDirectory, "routing", "resolved")), f => f.Contains("kept-unrouted")); // nothing deleted
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.NoteRoutingDeferred && r.DataString("reason") == "kept unrouted by user");
    }

    [Fact]
    public void AStaleRoutingDecisionWithoutItsNoteIsNotShown()
    {
        using var s = Scenario.New(_tmp, Mind).WithWorkspace()
            .Project("Atlas")
            .Project("Garden")
            .Note("Atlas and Garden both need a budget line before the board meeting.");
        var item = Assert.Single(s.Snap.Inbox);
        // Simulate a hand-deleted staging note: the pending decision file is orphaned.
        File.Delete(Path.Combine(_tmp.Root.DraftNotesDirectory, item.NoteId + ".json"));
        s.Restart();
        Assert.Empty(s.Snap.Inbox);
        Assert.Single(s.C.PendingRoutingDecisions); // still on disk for inspection, just not offered
    }

    [Fact]
    public void ConflictingDecisionsAreKeptDisputedUntilTheUserSupersedesOne()
    {
        using var s = Scenario.New(_tmp, Mind, mind: Recalling()).WithWorkspace()
            .Project("Atlas")
            .Note("We decided the Atlas beta ships on October 14.")
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.NoteRouted)
            .Note("We decided the Atlas beta ships on November 2 instead.")
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.NoteDisputed)
            .ExpectReview(ReviewItemKind.DisputedNotes);
        Assert.Contains("1 disputed", s.Snap.Receipt);

        var project = s.H.Registry.FindActive("atlas")!;
        var (notes, _) = ProjectNoteStore.ReadAll(project.RootPath);
        var older = notes.Single(n => n.Note.Body.Contains("October")).Note;
        var newer = notes.Single(n => n.Note.Body.Contains("November")).Note;
        Assert.Equal(NoteStatus.Active, older.Status);        // the earlier decision is untouched
        Assert.Equal(NoteStatus.Disputed, newer.Status);      // the newer one carries the link
        Assert.Equal([older.Id], newer.DisputedWith);

        // Recall shows both, and the dispute is on the record itself, so whatever reads it can say so.
        s.Command("What did I decide about the Atlas beta?")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("October 14")
            .ExpectAnswerContains("November 2")
            .ExpectAnswerContains("(disputed)");
        Assert.Equal(2, s.Response.Citations.Count(c => c.Kind == SearchIndex.NoteKind));

        // Resolve: the new decision supersedes the old one. Both versions stay on disk.
        var item = s.Snap.Review.Single(r => r.Kind == ReviewItemKind.DisputedNotes);
        s.Do("supersede", c => Assert.True(c.ResolveDispute(item.Payload!, newSupersedesExisting: true)))
            .ExpectEvent(EventTypes.NoteSuperseded);
        Assert.DoesNotContain(s.Snap.Review, r => r.Kind == ReviewItemKind.DisputedNotes);
        (notes, _) = ProjectNoteStore.ReadAll(project.RootPath);
        older = notes.Single(n => n.Note.Id == older.Id).Note;
        newer = notes.Single(n => n.Note.Id == newer.Id).Note;
        Assert.Equal(NoteStatus.Superseded, older.Status);
        Assert.Equal(NoteStatus.Active, newer.Status);
        Assert.Empty(newer.DisputedWith);
        Assert.Equal([older.Id], newer.Supersedes);
        Assert.True(Directory.EnumerateFiles(Path.Combine(project.RootPath, ProjectLayout.OrchestratorDirectoryName, "versions"), "*", SearchOption.AllDirectories).Count() >= 2);

        // Recall still finds the superseded decision, and finds it marked: nothing was erased.
        s.Command("What did I decide about the Atlas beta?")
            .ExpectAnswerContains("November 2")
            .ExpectAnswerContains("(superseded)");
    }

    [Fact]
    public void DisputeCanBeResolvedByKeepingBoth()
    {
        using var s = Scenario.New(_tmp, Mind).WithWorkspace()
            .Project("Atlas")
            .Note("We decided the Atlas beta ships on October 14.")
            .Note("We decided the Atlas beta ships on November 2 instead.")
            .ExpectReview(ReviewItemKind.DisputedNotes);
        var item = s.Snap.Review.Single(r => r.Kind == ReviewItemKind.DisputedNotes);
        s.Do("keep both", c => Assert.True(c.ResolveDispute(item.Payload!, newSupersedesExisting: false)))
            .ExpectEvent(EventTypes.NoteModified)
            .ExpectNoEvent(EventTypes.NoteSuperseded);
        Assert.DoesNotContain(s.Snap.Review, r => r.Kind == ReviewItemKind.DisputedNotes);
        var project = s.H.Registry.FindActive("atlas")!;
        var (notes, _) = ProjectNoteStore.ReadAll(project.RootPath);
        Assert.All(notes, n => Assert.Equal(NoteStatus.Active, n.Note.Status));
        Assert.All(notes, n => Assert.Empty(n.Note.DisputedWith));
    }

    [Fact]
    public void WithTheOrchestratorOffNotesStayVerbatimDraftsWithoutRouting()
    {
        using var s = Scenario.New(_tmp, configure: x => x.Orchestrator.Mode = OrchestratorSettings.Off).WithWorkspace()
            .Note("We decided the Atlas beta ships on October 14. Need to email the pilot customers.")
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.NoteDraftCreated, atLeast: 1)
            .ExpectNoEvent(EventTypes.NoteExtracted)
            .ExpectNoEvent(EventTypes.NoteRouted);
        var draft = Assert.Single(s.H.Notes.Unrouted());
        Assert.Equal(DraftNote.RawCaptureType, draft.Type);
        Assert.Contains("orchestrator is off", s.Snap.Receipt);
    }

    [Fact]
    public void ProjectPolicyCanRaiseTheBarForAutomaticFiling()
    {
        using var s = Scenario.New(_tmp, Mind).WithWorkspace().Project("Atlas");
        var project = s.H.Registry.FindActive("atlas")!;
        project.Policy.AutoRouteThreshold = 0.95; // a mention alone is no longer enough for this project
        s.H.Registry.Update(project);

        s.Note("The Atlas kickoff is on Monday.")
            .ExpectState(RelayState.Completed)
            .ExpectNoEvent(EventTypes.NoteRouted);
        var item = Assert.Single(s.Snap.Inbox);
        Assert.Equal("atlas", Assert.Single(item.Candidates).Slug); // offered, not filed
    }

    [Fact]
    public void RecallFindsUnroutedDraftsAndFiledNotesAlikeAndCitesWhatItOpened()
    {
        var mind = Recalling();
        using var s = Scenario.New(_tmp, Mind, mind: mind).WithWorkspace()
            .Project("Atlas")
            .Note("Atlas pricing will be tiered by seat count.")                 // filed
            .Note("Remember the dentist appointment is on the 22nd at noon.")     // unrouted draft
            .Command("what did I say about pricing?")
            .ExpectAnswerContains("seat count");
        Assert.Equal(SearchIndex.NoteKind, Assert.Single(s.Response.Citations).Kind);

        s.Command("when is the dentist?").ExpectAnswerContains("22nd");
        // Staging is searched beside the projects, but a draft is not a note that can be opened, so it grounds nothing.
        Assert.Contains(Hits(mind.Requests[^1]), h => h.Kind == SearchIndex.DraftKind);
        Assert.Empty(s.Response.Citations);
    }

    public void Dispose() => _tmp.Dispose();
}
