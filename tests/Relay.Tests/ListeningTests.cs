using Relay.Core.Attention;
using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Stream;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// Listening: the note chord opening a conversation the mind reads. What these tests hold Relay to is the
/// README's contract — a rolling window that expires continuously, excerpts anchored to the words that
/// triggered them, overlap kept by reference, a ledger that never holds the words, and raised work presented
/// by the attention arbiter, not dumped on the screen. The mind's own moves are held to in ObservingTests.
/// </summary>
public class ListeningTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private const string Decision = "We decided the Atlas beta ships on October 14.";
    private const string Chatter = "Anyway, how was the weekend, did you get out at all?";
    private const string LaunchEmail = "Marketing wants the launch email out a week before.";

    /// <summary>
    /// The deterministic grammar still plans what the mind raises (docs/10, step 1c). Listening stays off
    /// here so the note chord dictates while the world is built; <see cref="Scenario.WithListening"/> turns it on.
    /// </summary>
    private static void Planned(RelaySettings s) => s.Orchestrator.Mode = OrchestratorSettings.Rules;

    private static Action<RelaySettings> Planned(Action<RelaySettings> also) => s => { Planned(s); also(s); };

    // ----------------------------------------------------------------------------------------
    // Units: segmenter, buffer, guard
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void SegmenterCutsAtSentenceEndsAndOnQuiet()
    {
        var t0 = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var seg = new StreamSegmenter();

        Assert.Empty(seg.Feed("We decided the Atlas beta", t0));                      // no terminator yet
        var cut = seg.Feed("We decided the Atlas beta ships on October 14. Need to", t0.AddSeconds(2));
        Assert.Single(cut);
        Assert.Equal("We decided the Atlas beta ships on October 14.", cut[0].Text);
        Assert.Equal("Need to", seg.Pending);
        Assert.False(seg.HasPendingSince(t0.AddSeconds(2.5), TimeSpan.FromSeconds(1.2)));
        Assert.True(seg.HasPendingSince(t0.AddSeconds(4), TimeSpan.FromSeconds(1.2)));

        var flushed = seg.FlushPending(t0.AddSeconds(4));
        Assert.Single(flushed);
        Assert.Equal("Need to", flushed[0].Text);
        Assert.Equal("", seg.Pending);

        // The host trimmed the surface: only appended text counts, nothing is re-read.
        seg.SurfaceTrimmed("");
        Assert.Empty(seg.Feed("", t0.AddSeconds(5)));
        Assert.Single(seg.Feed("Fine. ", t0.AddSeconds(6)));
    }

    [Fact]
    public void BufferHoldsOnlyTheWindowAndForgetsWhatWasRead()
    {
        var t0 = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var buffer = new ConversationBuffer(TimeSpan.FromSeconds(90));
        var a = new StreamSegment("A", t0, "first");
        var b = new StreamSegment("B", t0.AddSeconds(50), "second");
        var c = new StreamSegment("C", t0.AddSeconds(100), "third");

        buffer.Append(a, t0);
        buffer.Append(b, t0.AddSeconds(50));
        Assert.Equal(new[] { "A", "B" }, buffer.UnreadIds());
        buffer.MarkRead(["A"]);
        Assert.Equal(new[] { "B" }, buffer.UnreadIds());

        buffer.Append(c, t0.AddSeconds(100));                                           // A is 100 s old: gone on touch
        Assert.Equal(new[] { "B", "C" }, buffer.Segments.Select(s => s.SegmentId));
        Assert.Equal(1, buffer.ExpiredSegments);
        Assert.Equal(3, buffer.TotalSegments);
        Assert.Equal(50, buffer.HeldSeconds);

        Assert.Equal(2, buffer.Expire(t0.AddSeconds(200)));                             // silence: everything expires
        Assert.Empty(buffer.Segments);
        Assert.Null(buffer.Find("B"));
    }

    [Fact]
    public void RetentionGuardBoundsOneExcerptAndTheFractionOfTalkKept()
    {
        var guard = new RetentionGuard(maxExcerptSeconds: 30, maxRetainedFraction: 0.25);

        Assert.False(guard.Decide(requestedSeconds: 12, retainedSecondsSoFar: 0, elapsedSeconds: 20).Shrunk);   // early and small
        Assert.True(guard.Decide(requestedSeconds: 31, retainedSecondsSoFar: 0, elapsedSeconds: 20).Shrunk);    // one excerpt too long
        Assert.False(guard.Decide(requestedSeconds: 20, retainedSecondsSoFar: 30, elapsedSeconds: 59).Shrunk);  // first minute exempt
        Assert.True(guard.Decide(requestedSeconds: 20, retainedSecondsSoFar: 30, elapsedSeconds: 120).Shrunk);  // 50/120 > 25 %
        Assert.False(guard.Decide(requestedSeconds: 5, retainedSecondsSoFar: 20, elapsedSeconds: 120).Shrunk);  // 25/120 < 25 %
    }

    // ----------------------------------------------------------------------------------------
    // Scenarios: the stream end to end
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The shipped default while the architecture is built (docs/09): the whole conversation is held until listening
    /// stops, and the mind reads stretches of it — a pass waits for enough new talk or for the oldest of it to age —
    /// instead of reading every fragment. Words still never reach the ledger.
    /// </summary>
    [Fact]
    public void WholeConversationIsHeldAndIngestedInStretchesNotFragments()
    {
        var mind = new ListeningMind().When("launch email", "remember", "Keep the launch email timing.", note: "The launch email goes out a week before the beta.", noteType: "decision");
        using var s = Scenario.New(_tmp, Planned(cfg => { cfg.Stream.BufferSeconds = StreamSettings.WholeConversation; cfg.Stream.MinIngestChars = 240; cfg.Stream.MinIngestSeconds = 20; }), mind: mind)
            .WithWorkspace()
            .WithListening().StartListening().ExpectListening()
            .Hear(Decision).Observe();                                                  // one short sentence, seconds old: held, not read yet
        var live = s.Snap.Listening!;
        Assert.Equal(0, live.WindowSeconds);
        Assert.Equal(0, live.Passes);
        Assert.Equal(1, live.HeldSegments);
        Assert.Empty(mind.Passes);

        s.Silence(TimeSpan.FromSeconds(25));                                             // …until it has waited long enough
        Assert.Equal(1, s.Snap.Listening!.Passes);
        Assert.Single(mind.Passes);

        s.Hear(Chatter).Hear(LaunchEmail).Observe();                                     // two more short sentences: under both thresholds again
        Assert.Equal(1, s.Snap.Listening!.Passes);
        s.Silence(TimeSpan.FromSeconds(200));                                            // nothing expired, and the stretch was read as one
        live = s.Snap.Listening!;
        Assert.Equal(3, live.HeldSegments);
        Assert.Equal(2, live.Passes);
        var window = mind.Passes[^1].Transcript.OfType<WindowObserved>().Last();
        Assert.Equal(2, window.Fresh.Count);                                             // Chatter and LaunchEmail arrived in one pass
        Assert.Equal(3, window.Lines.Count());                                           // over the whole conversation so far
        Assert.Equal(1, live.Raised);

        s.StopListening().ExpectListening(false);
        Assert.False(File.Exists(s.H.Root.CurrentStreamPath));
        var ledger = s.H.LedgerText();
        foreach (var sentence in new[] { Decision, Chatter, LaunchEmail }) Assert.DoesNotContain(sentence, ledger);
        Assert.True(s.H.Last(EventTypes.StreamStarted)!.DataBool("wholeConversation") == true);
    }

    /// <summary>A watched term does not wait for the slow ingest.</summary>
    [Fact]
    public void WatchedTermsAreReadAtOnceEvenWithSlowIngest()
    {
        var mind = new ListeningMind();
        using var s = Scenario.New(_tmp, Planned(cfg => { cfg.Stream.BufferSeconds = StreamSettings.WholeConversation; cfg.Stream.MinIngestChars = 5000; cfg.Stream.MinIngestSeconds = 300; }), mind: mind)
            .WithWorkspace()
            .Do("Always show Atlas", c => Assert.True(c.UpdatePreference("display.alwaysShow", "Atlas")))
            .WithListening().StartListening().Hear(Chatter).Observe();
        Assert.Equal(0, s.Snap.Listening!.Passes);
        s.Hear(Decision);
        Assert.Equal(1, s.Snap.Listening!.Passes);
    }

    /// <summary>The README's retention promise: words live in the buffer, expire on their own, and never reach the ledger.</summary>
    [Fact]
    public void ListeningKeepsNoWordsExpiresTheBufferAndLeavesOnlyMetadataBehind()
    {
        var mind = new ListeningMind();   // hears everything, raises nothing
        using var s = Scenario.New(_tmp, Planned, mind: mind).WithWorkspace()
            .WithListening().StartListening().ExpectState(RelayState.NoteCapture).ExpectListening()
            .Hear(Decision)
            .Hear(Chatter)
            .Hear(LaunchEmail)
            .Observe();

        var live = s.Snap.Listening!;
        Assert.Equal(3, live.TotalSegments);
        Assert.Equal(3, live.HeldSegments);
        Assert.Equal(90, live.WindowSeconds);
        Assert.True(live.Passes >= 1);
        Assert.Equal(0, live.Raised);

        s.Silence(TimeSpan.FromSeconds(100));
        live = s.Snap.Listening!;
        Assert.Equal(0, live.HeldSegments);                                          // the window emptied on its own
        Assert.Equal(3, live.TotalSegments);
        Assert.False(File.Exists(s.H.Root.CurrentDraftPath));                        // no dictation draft while streaming

        s.StopListening().ExpectState(RelayState.Completed).ExpectListening(false).ExpectTaskCount(0);
        Assert.StartsWith("Listened", s.Snap.Receipt);
        Assert.False(File.Exists(s.H.Root.CurrentStreamPath));

        var ledger = s.H.LedgerText();
        foreach (var sentence in new[] { Decision, Chatter, LaunchEmail })
            Assert.DoesNotContain(sentence, ledger);
        Assert.DoesNotContain("October 14", ledger);
        var segment = s.H.Last(EventTypes.StreamSegment)!;
        Assert.Equal(LaunchEmail.Length, segment.DataInt64("chars"));
        Assert.Equal(64, segment.DataString("sha256")!.Length);
        Assert.Equal(3, s.H.Count(EventTypes.StreamSegment));
        Assert.True(s.H.Count(EventTypes.ObserveChecked) >= 1);
        var stopped = s.H.Last(EventTypes.StreamStopped)!;
        Assert.Equal(3, stopped.DataInt64("segments"));
        Assert.Equal(3, stopped.DataInt64("expired"));
        Assert.Equal(0, stopped.DataInt64("excerpts"));
        Assert.Equal("stopped", stopped.DataString("reason"));
        Assert.Empty(s.H.Excerpts.All());
        Assert.Empty(s.Snap.Attention);
    }

    /// <summary>
    /// Found by the live model run: a model planner quotes the overheard sentence in a proposal's reason and in the note
    /// text it proposes. Proposal events are ledger events, so for an overheard task the reason, the expected effects and
    /// the prose in the target are fingerprinted like the plan's answer; ids, slugs, types and confidences stay legible.
    /// </summary>
    [Fact]
    public void APlannerThatQuotesTheOverheardWordsInAProposalLeavesNoWordsInTheLedger()
    {
        const string Heard = "Someone needs to find out whether Hull council requires a separate licence for the Lightshift pilot.";
        // The raise's why is a category, as the mind is told to write it (it is ledgered); the objective quotes the words.
        var mind = new ListeningMind().When("Hull council", "research",
            "Find out whether Hull council requires a separate licence for the Lightshift pilot.", project: "Lightshift", topic: "licensing");
        // The grammar builds the world (direct asks); the overheard task is planned by a stand-in for the model that quotes the words everywhere it can.
        var rules = new RuleBasedOrchestrator();
        var planner = new CannedOrchestrator();
        using var s = Scenario.New(_tmp, Planned, mind: mind, orchestrator: planner).WithWorkspace();
        planner.Otherwise((req, ctx) => req.Origin != TaskOrigin.Observed
            ? rules.PlanAsync(req, ctx, CancellationToken.None).GetAwaiter().GetResult()
            : new TurnPlan(true, "Someone must check with Hull council about the Lightshift pilot licence", ["Read the excerpt: " + Heard], "The room said: " + Heard, [],
                [new Proposal("01PROPOSALHULL000000000000", Actions.CreateDraftNote, "The excerpt says: " + Heard,
                    new Dictionary<string, string> { ["projectId"] = ctx.Registry.FindActive("lightshift")!.Id, ["type"] = "task", ["confidence"] = "0.9", ["text"] = "Find out whether Hull council requires a separate licence for the Lightshift pilot." },
                    ["01SOURCE000000000000000000"], ["A task note quoting: " + Heard], Risks.StagingWrite, false, Producers.Model)],
                "canned"));
        s.Command("create project Lightshift").Approve();
        var lightshift = s.H.Registry.FindActive("lightshift")!;

        s.WithListening().StartListening()
            .Hear(Chatter)
            .Hear(Heard).Observe()
            .ExpectTask(TaskKind.Research, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectEvent(EventTypes.ProposalReceived);

        var ledger = s.H.LedgerText();
        foreach (var words in new[] { Heard, "Hull council", "separate licence", "The room said", "The excerpt says", "quoting" })
            Assert.DoesNotContain(words, ledger, StringComparison.OrdinalIgnoreCase);

        var task = s.FindTask(TaskKind.Research)!;
        Assert.StartsWith("canned", task.Producer);
        var received = Assert.Single(s.H.Records(), r => r.Type == EventTypes.ProposalReceived && r.DataString("taskId") == task.TaskId);
        Assert.StartsWith("withheld: ", received.DataString("reason"));
        var target = received.Data.GetProperty("target");
        Assert.Equal(lightshift.Id, target.GetProperty("projectId").GetString());                 // references stay readable for the audit trail
        Assert.Equal("task", target.GetProperty("type").GetString());
        Assert.Equal("0.9", target.GetProperty("confidence").GetString());
        Assert.StartsWith("withheld: ", target.GetProperty("text").GetString());                 // prose does not
        Assert.All(received.Data.GetProperty("expectedEffects").EnumerateArray(), e => Assert.StartsWith("withheld: ", e.GetString()));
        var decided = Assert.Single(s.H.Records(), r => r.Type == EventTypes.ProposalDecided && r.DataString("taskId") == task.TaskId);
        Assert.StartsWith("withheld: ", decided.Data.GetProperty("target").GetProperty("text").GetString());
        // Tier A executes at once; the execution record names the note by id and fingerprints its text, while the journal keeps the target whole.
        var started = Assert.Single(s.H.Records(), r => r.Type == EventTypes.ExecutionStarted && r.DataString("turnId") == task.TaskId);
        Assert.StartsWith("withheld: ", started.Data.GetProperty("target").GetProperty("text").GetString());
        Assert.Equal(lightshift.Id, started.Data.GetProperty("target").GetProperty("projectId").GetString());
        s.ExpectEvent(EventTypes.NoteDraftCreated);

        // The proposal itself is intact where it is acted on: the task record and the pending proposal carry the words.
        Assert.Contains("Hull council", Assert.Single(task.Proposals).Target["text"]);
        Assert.Contains("Hull council", File.ReadAllText(Path.Combine(s.H.Root.TasksDirectory, task.TaskId + ".json")));
        // A typed instruction is the user's own words and stays in the ledger verbatim.
        Assert.Contains("create project Lightshift", ledger);
    }

    /// <summary>Work raised over a claim that conflicts with two stored decisions is the one thing that earns an alert.</summary>
    [Fact]
    public void AConflictingClaimKeepsAnExcerptRaisesAnObservedTaskAndAlerts()
    {
        // Each mention is its own objective (the loop refuses a repeat of one), and one merge key ties the cards together.
        var mind = new ListeningMind().When("November 2", line => new RaiseMove("check",
            $"Check the Atlas ship date claimed in line {line.Label} against stored decisions.", [line.Label],
            "a dated claim about a known project", Project: "Atlas", MergeKey: "check:atlas:ship-date"));
        var planner = new CannedOrchestrator();
        using var s = Scenario.New(_tmp, Planned, mind: mind, orchestrator: new CompositeOrchestrator(new RuleBasedOrchestrator(), planner)).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Note("We decided the Atlas beta ships on October 14.")
            .Note("The Atlas launch email goes out on October 7.");
        var atlas = s.H.Registry.FindActive("atlas")!;
        var stored = ProjectNoteStore.ReadAll(atlas.RootPath).Notes.Select(n => n.Note).ToList();
        Assert.Equal(2, stored.Count);
        planner.Otherwise((req, _) => req.Kind == TaskKind.Check
            ? new TurnPlan(true, "The claim conflicts with two stored decisions", ["Read atlas decisions"], "Stored: beta ships October 14 (email October 7); heard: November 2.",
                stored.Select(n => new Citation("note", n.Id, atlas.Id, atlas.Slug, n.Body, null)).ToList(), [], "canned", Consistent: false)
            : TurnPlan.NotUnderstood("canned", "not scripted"));

        s.WithListening().StartListening()
            .Hear(Chatter)
            .Hear("Actually Atlas ships on November 2 now.")
            .Observe()
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectExcerpts(1)
            .ExpectAttention(Presentation.Alert, "Conflict");

        var task = s.FindTask(TaskKind.Check)!;
        Assert.False(task.Foreground);
        Assert.Equal(TaskOrigin.Observed, task.Origin);
        Assert.False(task.Consistent);
        Assert.Equal(2, task.Citations.Count);
        Assert.Equal(Presentation.Alert, task.Presentation);
        Assert.NotNull(task.ExcerptId);
        Assert.Equal(RelayState.NoteCapture, s.Snap.State);                          // the stream is untouched by the task

        var excerpt = s.H.Excerpts.Read(task.ExcerptId!)!;
        Assert.Single(excerpt.Segments);                                              // anchored to the line the raise named
        Assert.Equal("Actually Atlas ships on November 2 now.", excerpt.Text);
        Assert.Equal(excerpt.TriggerSegmentId, excerpt.Segments[0].SegmentId);
        Assert.Equal("scripted", excerpt.SelectedBy);
        Assert.DoesNotContain(Chatter, s.H.LedgerText());                            // the sentence before it was never kept

        var created = s.H.Last(EventTypes.TaskCreated)!;
        Assert.Equal("observed", created.DataString("origin"));
        Assert.Equal("check", created.DataString("kind"));
        Assert.Equal(excerpt.ExcerptId, created.DataString("excerptId"));
        Assert.Equal("scripted", created.DataString("raisedBy"));
        var raised = s.H.Last(EventTypes.ObserveRaised)!;
        Assert.Equal("scripted", raised.DataString("by"));
        var shown = s.H.Last(EventTypes.AttentionShown)!;
        Assert.Equal("alert", shown.DataString("level"));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ExcerptStored && r.DataString("excerptId") == excerpt.ExcerptId);

        // The planner was asked the raise's objective, in the observed lane, with the excerpt in hand.
        var request = Assert.Single(planner.Requests, r => r.Kind == TaskKind.Check);
        Assert.Equal(TaskOrigin.Observed, request.Origin);
        Assert.Equal(excerpt.ExcerptId, request.ExcerptId);
        Assert.StartsWith("Check the Atlas ship date", request.Instruction);

        // Said again inside the cool-down: the card refreshes rather than multiplying; dismissed, it stays away.
        s.Hear("Yes, Atlas ships on November 2, I am sure.").Observe();
        var card = Assert.Single(s.Snap.Attention, a => a.Level == Presentation.Alert);
        Assert.Equal(2, card.Occurrences);
        Assert.Equal(2, card.TaskIds.Count);
        Assert.Equal(1, s.H.Count(EventTypes.TaskMerged));
        s.DismissAttention("Conflict").ExpectNoAttention(Presentation.Alert)
            .Hear("Atlas ships on November 2, as I said.").Observe()
            .ExpectNoAttention(Presentation.Alert);
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.AttentionSuppressed && r.DataString("reason")!.Contains("dismissed"));
        Assert.Equal(3, s.Snap.Tasks.Count(t => t.Kind == TaskKind.Check));         // every raise is still a task with a record
    }

    /// <summary>A statement that agrees with what is stored produces a task, a record, and no card at all.</summary>
    [Fact]
    public void AConsistentClaimIsRecordedAndShowsNothing()
    {
        var mind = new ListeningMind().When("October 14", "check", "Check the Atlas ship date.", project: "Atlas");
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Agrees with the stored decision", [], "Stored and heard agree: October 14.", [], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, Planned, mind: mind, orchestrator: planner).WithWorkspace()
            .WithListening().StartListening()
            .Hear(Decision).Observe()
            .ExpectTask(TaskKind.Check, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectNoAttention();

        var task = s.FindTask(TaskKind.Check)!;
        Assert.True(task.Consistent);
        Assert.Equal(Presentation.None, task.Presentation);
        Assert.Contains("agrees", task.PresentationReason);
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.AttentionSuppressed && r.DataString("taskId") == task.TaskId);
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.TaskCompleted && r.DataString("taskId") == task.TaskId);
        Assert.True(File.Exists(Path.Combine(s.H.Root.TasksDirectory, task.TaskId + ".json")), "the diagnostics record is written even when nothing is shown");
    }

    /// <summary>Two raises over the same words keep the text once: the later excerpt points at the earlier one.</summary>
    [Fact]
    public void OverlappingExcerptsReferenceEarlierSegmentsInsteadOfCopyingThem()
    {
        var mind = new ListeningMind()
            .When("ships on", "check", "Check the ship date.", project: "Atlas")
            .WhenWholeWindow("agreed", "check", "Check the email date against the ship date.", project: "Atlas");
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Checked", [], "Consistent.", [], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, Planned, mind: mind, orchestrator: planner).WithWorkspace()
            .WithListening().StartListening()
            .Hear(Decision).Observe()
            .ExpectExcerpts(1)
            .Hear(LaunchEmail)
            .Hear("So the email goes out on October 7, agreed.").Observe()
            .ExpectExcerpts(2);

        var excerpts = s.H.Excerpts.All().OrderBy(e => e.CreatedAt).ToList();
        var first = excerpts[0];
        var second = excerpts[1];
        Assert.Equal(Decision, first.Text);
        Assert.Equal(2, second.Segments.Count);                                        // only the two new sentences are copied
        Assert.Equal(LaunchEmail, second.Segments[0].Text);
        var reference = Assert.Single(second.References);
        Assert.Equal(first.ExcerptId, reference.ExcerptId);
        Assert.Equal(first.Segments.Select(x => x.SegmentId), reference.SegmentIds);
        Assert.Equal(Decision.Length + LaunchEmail.Length + "So the email goes out on October 7, agreed.".Length, s.H.Excerpts.RetainedChars());

        var stored = s.H.Last(EventTypes.ExcerptStored)!;
        Assert.Equal(2, stored.DataInt64("segments"));
        Assert.Equal(1, stored.DataInt64("referenced"));
        Assert.Equal(false, stored.DataBool("shrunkByGuard"));
        Assert.Equal(2, s.Snap.Listening!.Excerpts);
    }

    /// <summary>An excerpt longer than the per-excerpt bound shrinks to the trigger sentence, and the ledger says so.</summary>
    [Fact]
    public void TheRetentionGuardShrinksAnOverlongExcerptToTheTrigger()
    {
        var mind = new ListeningMind().WhenWholeWindow("agreed", "check", "Check what was agreed.", project: "Atlas");
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Checked", [], "Consistent.", [], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, Planned, mind: mind, orchestrator: planner).WithWorkspace()
            .WithListening().StartListening()
            .Hear(Decision)
            .Silence(TimeSpan.FromSeconds(40))                                        // 40 s apart: the pair would exceed the 30 s bound
            .Hear("So the email goes out on October 7, agreed.").Observe()
            .ExpectExcerpts(1);

        var excerpt = Assert.Single(s.H.Excerpts.All());
        Assert.True(excerpt.ShrunkByGuard);
        Assert.Single(excerpt.Segments);
        Assert.Equal("So the email goes out on October 7, agreed.", excerpt.Text);
        Assert.Empty(excerpt.References);
        Assert.Equal(true, s.H.Last(EventTypes.ExcerptStored)!.DataBool("shrunkByGuard"));
        Assert.Equal(2, s.Snap.Listening!.HeldSegments);                              // the first sentence is still in the buffer, just not kept
    }

    /// <summary>A pass the mind cannot complete is reported and abandoned; the stretch it failed on is not retried, and listening goes on.</summary>
    [Fact]
    public void AFailedPassIsReportedAndListeningCarriesOn()
    {
        var mind = new ListeningMind { Throws = new InvalidOperationException("llama.cpp is not running") };
        mind.When("launch email", "remember", "Keep the launch email timing.", note: "The launch email goes out a week before.", noteType: "decision");
        using var s = Scenario.New(_tmp, Planned, mind: mind).WithWorkspace()
            .WithListening().StartListening()
            .Hear(Decision).Observe()
            .ExpectEvent(EventTypes.ObserveFailed)
            .ExpectTaskCount(0)
            .ExpectExcerpts(0);

        Assert.Equal("llama.cpp is not running", s.Snap.Listening!.LastError);
        Assert.Equal("llama.cpp is not running", s.H.Last(EventTypes.ObserveFailed)!.DataString("error"));
        Assert.Equal(1, s.Snap.Listening!.Passes);
        Assert.False(s.Snap.Listening!.Reading);

        // The model comes back: the next pass is read, and the error clears.
        mind.Throws = null;
        s.Hear(LaunchEmail).Observe()
            .ExpectTask(TaskKind.Remember, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectEvent(EventTypes.NoteDraftCreated);
        Assert.Null(s.Snap.Listening!.LastError);
        Assert.Equal("scripted", s.H.Last(EventTypes.ObserveRaised)!.DataString("by"));
        Assert.DoesNotContain(Decision, s.H.LedgerText());
    }

    [Fact]
    public void AMindThatNeverAnswersIsTimedOutAndTheStreamGoesOn()
    {
        var mind = new HangingMind();
        using var s = Scenario.New(_tmp, Planned(x => x.Listening.PassTimeoutMs = 3_000), mind: mind).WithWorkspace()
            .WithListening().StartListening()
            .Hear(Chatter)
            .Silence(TimeSpan.FromSeconds(8))
            .ExpectEvent(EventTypes.ObserveFailed)
            .ExpectListening();

        Assert.Equal("the pass timed out after 3000 ms", s.H.Last(EventTypes.ObserveFailed)!.DataString("error"));
        Assert.False(s.Snap.Listening!.Reading);
        Assert.True(mind.Calls >= 1);
        s.Hear(LaunchEmail).Silence(TimeSpan.FromSeconds(8));
        Assert.True(s.Snap.Listening!.Passes >= 2, "the stream keeps reading after a timeout");
        s.StopListening().ExpectState(RelayState.Completed).ExpectListening(false);
    }

    /// <summary>A watched term is resolved the moment it is heard, pinned, and refreshed in place on every later mention.</summary>
    [Fact]
    public void AWatchedTermIsResolvedAtOnceAndPinned()
    {
        var mind = new ListeningMind().When("SLA", line => new RaiseMove("resolve",
            $"Define SLA as it is used in line {line.Label}.", [line.Label], "an acronym the user watches", Topic: "SLA", MergeKey: "define:sla"));
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Defined SLA", [], "SLA: service level agreement — the uptime and response commitments in the Atlas contract.", [], [], "canned"));
        using var s = Scenario.New(_tmp, Planned, mind: mind, orchestrator: planner).WithWorkspace()
            .Do("Always show SLA", c => Assert.True(c.UpdatePreference("display.alwaysShow", "SLA")))
            .ExpectPreference("display.alwaysShow", "SLA")
            .ExpectEvent(EventTypes.ChangeSetApplied)
            .WithListening().StartListening()
            .Hear("Our SLA promises four nines this quarter.");

        // No observe interval was waited for: the watched term triggered a pass immediately.
        s.ExpectTask(TaskKind.Resolve, TaskStatus.Completed, TaskOrigin.Observed).ExpectAttention(Presentation.Result, "SLA");
        var card = Assert.Single(s.Snap.Attention);
        Assert.True(card.Pinned);
        Assert.Equal("SLA", card.Title);
        Assert.Contains("service level agreement", card.Detail);
        Assert.Contains("watched term", s.FindTask(TaskKind.Resolve)!.PresentationReason);

        s.Hear(Chatter).Observe()
            .Hear("And the SLA penalty clause kicks in after two breaches.").Observe();
        card = Assert.Single(s.Snap.Attention);
        Assert.Equal(2, card.Occurrences);
        Assert.True(card.Pinned);
        Assert.Equal(2, s.Snap.Tasks.Count(t => t.Kind == TaskKind.Resolve));
        Assert.Equal(1, s.H.Count(EventTypes.TaskMerged));
    }

    /// <summary>The ask box works while listening: the ask is its own task beside the stream and its answer becomes a card.</summary>
    [Fact]
    public void ADirectAskWhileListeningRunsBesideTheStream()
    {
        using var s = Scenario.New(_tmp, Planned, mind: new ListeningMind()).WithWorkspace()
            .Command("create project Atlas").Approve()
            .Note(Decision)
            .WithListening().StartListening()
            .Hear(Chatter)
            .Ask("what did I say about the beta?")
            .ExpectState(RelayState.NoteCapture)
            .ExpectListening()
            .ExpectTask(TaskKind.Answer, TaskStatus.Completed, TaskOrigin.Direct)
            .ExpectAttention(Presentation.Result, "beta");

        var task = s.FindTask(TaskKind.Answer, origin: TaskOrigin.Direct)!;
        Assert.False(task.Foreground);
        Assert.Contains("October 14", task.Answer);
        Assert.Equal("ask", task.Lane);
        Assert.Equal(true, s.H.Last(EventTypes.AskRecorded)!.DataBool("whileListening"));
        Assert.Equal(false, s.H.Last(EventTypes.AskRecorded)!.DataBool("foreground"));
        Assert.NotEqual(task.TaskId, s.Snap.Response?.TaskId);                       // nothing took the foreground away from the stream

        s.Hear(LaunchEmail).Observe().ExpectListening()
            .StopListening().ExpectState(RelayState.Completed).ExpectListening(false);
        Assert.StartsWith("Listened", s.Snap.Receipt);
    }

    /// <summary>A crash mid-stream leaves a window file behind; on restart its size is recorded and its words are discarded.</summary>
    [Fact]
    public void ACrashWhileListeningDiscardsTheWindowAndRecordsOnlyItsSize()
    {
        using var s = Scenario.New(_tmp, Planned, mind: new ListeningMind()).WithWorkspace()
            .WithListening().StartListening()
            .Hear(Decision)
            .Hear(Chatter)
            .Silence(TimeSpan.FromSeconds(1));                                        // past the persist debounce
        Assert.True(File.Exists(s.H.Root.CurrentStreamPath), "the window is persisted (debounced) for crash accounting");
        Assert.False(File.Exists(s.H.Root.CurrentDraftPath), "a stream never writes a dictation draft");

        s.CrashAndRestart()
            .ExpectEvent(EventTypes.StreamInterruptedFound)
            .ExpectListening(false)
            .ExpectState(RelayState.Idle);
        var found = s.H.Last(EventTypes.StreamInterruptedFound)!;
        Assert.Equal(2, found.DataInt64("segments"));
        Assert.Equal(Decision.Length + Chatter.Length, found.DataInt64("chars"));
        Assert.Equal(true, found.DataBool("discarded"));
        Assert.False(File.Exists(s.H.Root.CurrentStreamPath));
        Assert.DoesNotContain(Decision, s.H.LedgerText());
        Assert.DoesNotContain(s.Snap.Review, r => r.Kind == ReviewItemKind.InterruptedCapture);
    }

    /// <summary>With listening off the note chord is what it always was: dictation, organized when it settles.</summary>
    [Fact]
    public void WithListeningOffTheNoteChordDictates()
    {
        using var s = Scenario.New(_tmp, Planned).WithWorkspace()
            .Command("create project Atlas").Approve()
            .StartListening();
        Assert.Null(s.Snap.Listening);
        Assert.False(s.Snap.ListeningEnabled);
        s.Do("dictate", c => c.TextChanged(Decision)).StopListening()
            .ExpectEvent(EventTypes.NoteRouted)
            .ExpectNoEvent(EventTypes.StreamStarted);
    }

    /// <summary>Listening on with no mind to read with: the chord dictates, and Relay says why rather than pretending.</summary>
    [Fact]
    public void ListeningOnWithNoMindDictatesAndSaysSo()
    {
        using var s = Scenario.New(_tmp, s => { s.Orchestrator.Mode = OrchestratorSettings.Mind; s.Listening.Enabled = true; })
            .WithWorkspace()
            .StartListening();
        Assert.Null(s.Snap.Listening);
        Assert.False(s.Snap.ListeningEnabled);
        Assert.False(s.Snap.MindReady);
        Assert.Contains(s.Snap.Review, r => r.Kind == ReviewItemKind.MindUnavailable);
    }

    public void Dispose() => _tmp.Dispose();
}
