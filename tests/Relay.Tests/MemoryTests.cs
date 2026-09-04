using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Memory;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Tests.Support;

namespace Relay.Tests;

/// <summary>Phase 6: extraction with exact spans, confidence routing, disputes and supersession, recall over all of it.</summary>
public class MemoryTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    /// <summary>The README's scenario, verbatim, so the documentation cannot drift from the behaviour.</summary>
    [Fact]
    public void ReadmeScenarioHolds()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
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
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
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
    public void NoteModeLeavesUnmentionedNotesUnroutedAndPutsAmbiguousOnesInReview()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("create project Garden").Approve()
            .Note("Buy compost and a new rake at the hardware store this weekend.")
            .ExpectState(RelayState.Completed)
            .ExpectEvent(EventTypes.NoteRoutingDeferred)
            .ExpectNoEvent(EventTypes.NoteRouted);
        Assert.Contains("unrouted", s.Snap.Receipt);
        Assert.Single(s.H.Notes.Unrouted());
        Assert.DoesNotContain(s.Snap.Review, r => r.Kind == ReviewItemKind.RoutingDecision); // no evidence → nothing to ask

        s.Note("Atlas and Garden both need a budget line before the board meeting.")
            .ExpectState(RelayState.Completed)
            .ExpectReview(ReviewItemKind.RoutingDecision)
            .ExpectNoEvent(EventTypes.NoteRouted);
        Assert.Contains("need your routing decision", s.Snap.Receipt);
        var item = s.Snap.Review.Single(r => r.Kind == ReviewItemKind.RoutingDecision);
        Assert.Contains("atlas", item.Detail);
        Assert.Contains("garden", item.Detail);
        Assert.Contains("ambiguous", item.Detail);

        // The user resolves it: the note is filed by the same controlled path with confidence 1 and the item leaves Review.
        var noteId = item.Payload!;
        var garden = s.H.Registry.FindActive("garden")!;
        s.Do("route to garden", c => Assert.True(c.RouteDraftNote(noteId, garden.Id)))
            .ExpectEvent(EventTypes.NoteRouted)
            .ExpectEvent(EventTypes.ApprovalGranted); // the click is recorded as the approval
        Assert.DoesNotContain(s.Snap.Review, r => r.Kind == ReviewItemKind.RoutingDecision);
        var (notes, _) = ProjectNoteStore.ReadAll(garden.RootPath);
        Assert.Contains(notes, n => n.Note.Id == noteId && n.Note.Confidence == 1);
        Assert.Single(s.H.Notes.Unrouted()); // the compost note is still waiting, untouched
    }

    [Fact]
    public void PendingRoutingDecisionsSurviveRestartAndCanBeKeptUnrouted()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Command("create project Garden").Approve()
            .Note("Atlas and Garden both need a budget line before the board meeting.")
            .ExpectReview(ReviewItemKind.RoutingDecision)
            .Restart()
            .ExpectReview(ReviewItemKind.RoutingDecision);

        var noteId = s.Snap.Review.Single(r => r.Kind == ReviewItemKind.RoutingDecision).Payload!;
        s.Do("keep unrouted", c => c.KeepUnrouted(noteId));
        Assert.DoesNotContain(s.Snap.Review, r => r.Kind == ReviewItemKind.RoutingDecision);
        Assert.Single(s.H.Notes.Unrouted());
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(_tmp.Root.ReviewDirectory, "routing", "resolved")), f => f.Contains("kept-unrouted")); // nothing deleted
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.NoteRoutingDeferred && r.DataString("reason") == "kept unrouted by user");
    }

    [Fact]
    public void ConflictingDecisionsAreKeptDisputedUntilTheUserSupersedesOne()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
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

        // Recall shows both, and says which is disputed.
        s.Command("What did I decide about the Atlas beta?")
            .ExpectState(RelayState.Completed)
            .ExpectAnswerContains("October 14")
            .ExpectAnswerContains("November 2")
            .ExpectAnswerContains("(disputed)");

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

        // Recall still finds the superseded decision, marked, ranked below the current one.
        s.Command("What did I decide about the Atlas beta?")
            .ExpectAnswerContains("November 2")
            .ExpectAnswerContains("(superseded)");
        var answer = s.Response.Answer!;
        Assert.True(answer.IndexOf("November 2", StringComparison.Ordinal) < answer.IndexOf("October 14", StringComparison.Ordinal), answer);
    }

    [Fact]
    public void DisputeCanBeResolvedByKeepingBoth()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
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
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve();
        var project = s.H.Registry.FindActive("atlas")!;
        project.Policy.AutoRouteThreshold = 0.95; // a mention alone (0.7) is no longer enough for this project
        s.H.Registry.Update(project);

        s.Note("The Atlas kickoff is on Monday.")
            .ExpectState(RelayState.Completed)
            .ExpectNoEvent(EventTypes.NoteRouted)
            .ExpectReview(ReviewItemKind.RoutingDecision);
    }

    [Fact]
    public void RecallFindsUnroutedDraftsAndFiledNotesAlikeWithCitations()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Note("Atlas pricing will be tiered by seat count.")                 // filed
            .Note("Remember the dentist appointment is on the 22nd at noon.")     // unrouted draft
            .Command("what did I say about pricing?")
            .ExpectAnswerContains("seat count")
            .Command("when is the dentist?")
            .ExpectAnswerContains("22nd");
        Assert.Contains(s.Response.Citations, c => c.Kind == "draft");
    }

    public void Dispose() => _tmp.Dispose();
}
