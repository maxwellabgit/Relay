using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Attention;
using Relay.Core.Config;
using Relay.Core.Judge;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Stream;
using Relay.Core.Tasks;
using Relay.Tests.Support;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests;

/// <summary>
/// Listening: the note chord with the judge on. What these tests hold Relay to is the README's
/// contract — a rolling window that expires continuously, excerpts anchored to the words that
/// triggered them, overlap kept by reference, a ledger that never holds the words, and findings that
/// become observed tasks presented by the attention arbiter, not dumped on the screen.
/// </summary>
public class ListeningTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private const string Decision = "We decided the Atlas beta ships on October 14.";
    private const string Chatter = "Anyway, how was the weekend, did you get out at all?";
    private const string LaunchEmail = "Marketing wants the launch email out a week before.";

    // ----------------------------------------------------------------------------------------
    // Units: segmenter, buffer, guard, judges
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
    public void BufferHoldsOnlyTheWindowAndForgetsWhatItJudged()
    {
        var t0 = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var buffer = new ConversationBuffer(TimeSpan.FromSeconds(90));
        var a = new StreamSegment("A", t0, "first");
        var b = new StreamSegment("B", t0.AddSeconds(50), "second");
        var c = new StreamSegment("C", t0.AddSeconds(100), "third");

        buffer.Append(a, t0);
        buffer.Append(b, t0.AddSeconds(50));
        Assert.Equal(new[] { "A", "B" }, buffer.UnjudgedIds());
        buffer.MarkJudged(["A"]);
        Assert.Equal(new[] { "B" }, buffer.UnjudgedIds());

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

    [Fact]
    public void HeuristicJudgeNamesTheLanesFromCuesAndSaysSo()
    {
        var judge = new HeuristicJudge();
        var t0 = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var context = new JudgeContext(["Atlas (atlas)"], ["SLA"], [], null);
        var window = new List<StreamSegment>
        {
            new("S1", t0, Decision),
            new("S2", t0.AddSeconds(3), Chatter),
            new("S3", t0.AddSeconds(6), "The OKR review is on Thursday."),
            new("S4", t0.AddSeconds(9), "Our SLA promises four nines."),
        };
        var decision = judge.JudgeAsync(new JudgeRequest(TaskOrigin.Observed, window, window.Select(s => s.SegmentId).ToList(), null, context, t0.AddSeconds(10)), CancellationToken.None).Result;

        Assert.Equal(HeuristicJudge.ProducerName, decision.Producer);
        var kinds = decision.Findings.Select(f => (f.Kind, f.SegmentIds[0])).ToList();
        Assert.Contains((TaskKind.Check, "S1"), kinds);                    // dated claim about a known project
        Assert.Contains((TaskKind.Remember, "S1"), kinds);                 // and a decision worth keeping
        Assert.DoesNotContain(kinds, k => k.Item2 == "S2");                // chatter is not a finding
        Assert.Contains((TaskKind.Resolve, "S3"), kinds);                  // an acronym
        var watched = Assert.Single(decision.Findings, f => f.SegmentIds[0] == "S4");
        Assert.Equal(TaskKind.Resolve, watched.Kind);
        Assert.Equal("SLA", watched.Topic);
        Assert.Equal("define:sla", watched.MergeKey);
        Assert.True(watched.Confidence > 0.8);                             // watched terms are near-certain

        var direct = judge.JudgeAsync(new JudgeRequest(TaskOrigin.Direct, [], [], "from now on keep answers brief", context, t0), CancellationToken.None).Result;
        Assert.Equal(TaskKind.Improve, Assert.Single(direct.Findings).Kind);
    }

    [Fact]
    public void ModelJudgeAsksForSchemaConstrainedJsonAndKeepsOnlyGroundedFindings()
    {
        var t0 = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var client = new ScriptedModelClient().Reply(new JsonObject
        {
            ["findings"] = new JsonArray(
                Finding("check", 0.82, "Atlas ship date stated", ["S1"], project: "Atlas", mergeKey: "check:atlas:oct-14"),
                Finding("remember", 0.40, "too unsure", ["S1"]),                      // below the judge's floor
                Finding("resolve", 0.90, "points at nothing in the window", ["Z9"]),   // hallucinated segment id
                Finding("banana", 0.95, "not a kind we know", ["S2"])),
        });
        var judge = new ModelJudge(client, maxOutputTokens: 600, minConfidence: 0.55);
        var window = new List<StreamSegment> { new("S1", t0, Decision), new("S2", t0.AddSeconds(3), Chatter) };
        var context = new JudgeContext(["Atlas (atlas)"], [], ["beta"], "Answer briefly.");

        var decision = judge.JudgeAsync(new JudgeRequest(TaskOrigin.Observed, window, ["S2", "S1"], null, context, t0.AddSeconds(4)), CancellationToken.None).Result;

        var request = Assert.Single(client.Requests);
        Assert.True(request.JsonObject);
        Assert.Equal(ModelJudge.Schema, request.JsonSchema);
        Assert.Equal("judge_decision", request.SchemaName);
        Assert.Equal(600, request.MaxOutputTokens);
        Assert.Contains("NEW [S1", request.Messages[1].Content);
        Assert.Contains("Active projects: Atlas (atlas)", request.Messages[1].Content);
        Assert.Contains("User preferences: Answer briefly.", request.Messages[0].Content);
        Assert.DoesNotContain("file", request.Messages[0].Content.Split(' ').Select(w => w.Trim('.', ',')).Where(w => w == "files")); // the judge never sees files

        Assert.Null(decision.Error);
        Assert.Equal("model:test-model", decision.Producer);
        Assert.Equal(100, decision.PromptTokens);
        Assert.Equal(2, decision.Findings.Count);
        var check = decision.Findings[0];
        Assert.Equal(TaskKind.Check, check.Kind);
        Assert.Equal("Atlas", check.ProjectHint);
        Assert.Equal("check:atlas:oct-14", check.MergeKey);
        Assert.Equal(["S1"], check.SegmentIds);
        Assert.Equal(TaskKind.Answer, decision.Findings[1].Kind);          // an unknown kind degrades to answer, it is not dropped

        var broken = new ModelJudge(new ScriptedModelClient().Reply("this is not json"));
        var failed = broken.JudgeAsync(new JudgeRequest(TaskOrigin.Observed, window, ["S1"], null, context, t0), CancellationToken.None).Result;
        Assert.Empty(failed.Findings);
        Assert.StartsWith("Judge returned something other than the contract", failed.Error);
        Assert.Equal("this is not json", failed.Raw);

        var down = new ModelJudge(new ScriptedModelClient().Fail("connection refused", 0));
        Assert.Equal("connection refused", down.JudgeAsync(new JudgeRequest(TaskOrigin.Observed, window, ["S1"], null, context, t0), CancellationToken.None).Result.Error);
    }

    private static JsonObject Finding(string kind, double confidence, string summary, string[] segments, string? project = null, string? mergeKey = null)
        => new()
        {
            ["kind"] = kind,
            ["confidence"] = confidence,
            ["summary"] = summary,
            ["why"] = "test",
            ["focused_prompt"] = "Check: " + summary,
            ["segment_ids"] = new JsonArray(segments.Select(s => (JsonNode?)s).ToArray()),
            ["project"] = project,
            ["merge_key"] = mergeKey,
        };

    // ----------------------------------------------------------------------------------------
    // Scenarios: the stream end to end
    // ----------------------------------------------------------------------------------------

    /// <summary>The README's retention promise: words live in the buffer, expire on their own, and never reach the ledger.</summary>
    [Fact]
    public void ListeningKeepsNoWordsExpiresTheBufferAndLeavesOnlyMetadataBehind()
    {
        var judge = new ScriptedJudge(); // hears everything, finds nothing
        using var s = Scenario.New(_tmp, judge: judge).WithWorkspace().WithListening()
            .StartListening().ExpectState(RelayState.NoteCapture).ExpectListening()
            .Hear(Decision)
            .Hear(Chatter)
            .Hear(LaunchEmail)
            .Observe();

        var live = s.Snap.Listening!;
        Assert.Equal(3, live.TotalSegments);
        Assert.Equal(3, live.HeldSegments);
        Assert.Equal(90, live.WindowSeconds);
        Assert.True(live.JudgePasses >= 1);
        Assert.Equal(0, live.Findings);

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
    /// The desktop smoke scenario with the real heuristic judge: a decision about a known project is filed, a task
    /// with no project waits in the inbox, and not one word of either sentence reaches the ledger — the judge's
    /// titles, the routing records and the tool calls carry fingerprints; the task records carry the text.
    /// </summary>
    [Fact]
    public void TheHeuristicJudgeLeavesNoWordsOfWhatItHeardInTheLedger()
    {
        const string Errand = "Remember to buy compost for the garden this weekend.";
        using var s = Scenario.New(_tmp, judge: new HeuristicJudge()).WithWorkspace()
            .Command("create project Atlas").Approve()
            .WithListening().StartListening()
            .Hear(Decision).Observe()
            .Hear(Errand).Observe()
            .ExpectEvent(EventTypes.NoteRouted)                                       // the decision named Atlas
            .ExpectEvent(EventTypes.NoteRoutingDeferred)                              // the errand named nothing
            .ExpectTask(TaskKind.Remember, TaskStatus.Completed, TaskOrigin.Observed)
            .StopListening().ExpectState(RelayState.Completed);

        var ledger = s.H.LedgerText();
        foreach (var words in new[] { Decision, Errand, "October 14", "compost", "garden", "beta ships" })
            Assert.DoesNotContain(words, ledger, StringComparison.OrdinalIgnoreCase);

        var created = s.H.Records().Where(r => r.Type == EventTypes.TaskCreated && r.DataString("origin") == "observed").ToList();
        Assert.NotEmpty(created);
        Assert.All(created, r =>
        {
            Assert.Equal(true, r.DataBool("overheard"));
            Assert.StartsWith("withheld: ", r.DataString("title"));                   // length and hash, never the words
            Assert.NotNull(r.DataString("why"));                                       // the cue is a fixed vocabulary and stays readable
        });
        Assert.All(s.H.Records().Where(r => r.Type == EventTypes.ObserveFound), r => Assert.DoesNotContain("Remember to", r.Data.GetRawText()));

        // The words live where the diagnostics drawer reads them: the task record and the excerpt, both under retention.
        var errand = s.Snap.Tasks.Single(t => t.Kind == TaskKind.Remember && t.Title!.Contains("compost"));
        Assert.Contains("compost", File.ReadAllText(Path.Combine(s.H.Root.TasksDirectory, errand.TaskId + ".json")));
        Assert.Equal(Errand, s.H.Excerpts.Read(errand.ExcerptId!)!.Text);
        Assert.Contains(s.Snap.Inbox, i => i.Text.Contains("compost"));
        // A typed instruction is the user's own words and stays in the ledger verbatim.
        Assert.Contains("create project Atlas", ledger);
    }

    /// <summary>A check finding that conflicts with two stored decisions is the one thing that earns an alert.</summary>
    [Fact]
    public void AConflictingClaimKeepsAnExcerptRaisesAnObservedTaskAndAlerts()
    {
        var judge = new ScriptedJudge().When("November 2", TaskKind.Check, "Check the claimed Atlas ship date against stored decisions.", projectHint: "Atlas", mergeKey: "check:atlas:ship-date");
        var planner = new CannedOrchestrator();
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: new CompositeOrchestrator(new Relay.Core.Orchestration.RuleBasedOrchestrator(), planner)).WithWorkspace()
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
        Assert.Single(excerpt.Segments);                                              // anchored to the trigger sentence only
        Assert.Equal("Actually Atlas ships on November 2 now.", excerpt.Text);
        Assert.Equal(excerpt.TriggerSegmentId, excerpt.Segments[0].SegmentId);
        Assert.Equal("scripted", excerpt.SelectedBy);
        Assert.DoesNotContain(Chatter, s.H.LedgerText());                            // the sentence before it was never kept

        var created = s.H.Last(EventTypes.TaskCreated)!;
        Assert.Equal("observed", created.DataString("origin"));
        Assert.Equal("check", created.DataString("kind"));
        Assert.Equal(excerpt.ExcerptId, created.DataString("excerptId"));
        Assert.Equal("scripted", created.DataString("judge"));
        var found = s.H.Last(EventTypes.ObserveFound)!;
        Assert.Equal("scripted", found.DataString("judge"));
        var shown = s.H.Last(EventTypes.AttentionShown)!;
        Assert.Equal("alert", shown.DataString("level"));
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ExcerptStored && r.DataString("excerptId") == excerpt.ExcerptId);

        // The planner was asked the judge's focused prompt, in the observed lane, with the excerpt in hand.
        var request = Assert.Single(planner.Requests, r => r.Kind == TaskKind.Check);
        Assert.Equal(TaskOrigin.Observed, request.Origin);
        Assert.Equal(excerpt.ExcerptId, request.ExcerptId);
        Assert.StartsWith("Check the claimed Atlas ship date", request.Instruction);

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
        Assert.Equal(3, s.Snap.Tasks.Count(t => t.Kind == TaskKind.Check));         // every finding is still a task with a record
    }

    /// <summary>A statement that agrees with what is stored produces a task, a record, and no card at all.</summary>
    [Fact]
    public void AConsistentClaimIsRecordedAndShowsNothing()
    {
        var judge = new ScriptedJudge().When("October 14", TaskKind.Check, "Check the Atlas date.", projectHint: "Atlas");
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Agrees with the stored decision", [], "Stored and heard agree: October 14.", [], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: planner).WithWorkspace().WithListening()
            .StartListening()
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

    /// <summary>Remember findings skip the planner: the judge's restatement is filed as a note under the project it named, with the excerpt as its source.</summary>
    [Fact]
    public void ARememberFindingFilesTheNoteUnderTheNamedProjectWithTheExcerptAsSource()
    {
        var judge = new ScriptedJudge().When("ships on", TaskKind.Remember, "Keep this decision.", projectHint: "Atlas", noteText: "Atlas beta ships on October 14.", topic: "atlas beta");
        using var s = Scenario.New(_tmp, judge: judge).WithWorkspace()
            .Command("create project Atlas").Approve()
            .WithListening().StartListening()
            .Hear(Decision).Observe()
            .ExpectTask(TaskKind.Remember, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectEvent(EventTypes.NoteRouted)
            .ExpectAttention(Presentation.Ambient, "Note filed")
            .ExpectExcerpts(1);

        var task = s.FindTask(TaskKind.Remember)!;
        Assert.Equal("executed", task.Outcome);
        var routing = Assert.Single(task.Proposals);
        Assert.Equal(Actions.RouteNote, routing.Action);
        Assert.Equal("executed", routing.Status);

        var atlas = s.H.Registry.FindActive("atlas")!;
        var note = Assert.Single(ProjectNoteStore.ReadAll(atlas.RootPath).Notes).Note;
        Assert.Equal("Atlas beta ships on October 14.", note.Body);
        var span = Assert.Single(note.Spans);
        Assert.Equal(task.ExcerptId, span.EventId);                                   // the source is the excerpt, not a ledger event
        var excerpt = s.H.Excerpts.Read(task.ExcerptId!)!;
        Assert.Equal(Decision, excerpt.Text[span.Start..span.End]);

        Assert.Equal("Judge", s.H.Last(EventTypes.NoteDraftCreated)!.DataString("by"), ignoreCase: true);
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.ProposalReceived && r.DataString("proposedBy") == "judge");
        Assert.Contains(s.H.Records(), r => r.Type == EventTypes.AttentionShown && r.DataString("level") == "ambient");
        Assert.DoesNotContain(s.H.Records(), r => r.Type == EventTypes.TaskPlanned && r.DataString("taskId") == task.TaskId); // no planner
        s.StopListening();
        Assert.Contains("1 task(s) raised", s.Snap.Receipt);
        Assert.Contains("1 excerpt(s) kept", s.Snap.Receipt);
    }

    /// <summary>Two findings over the same words keep the text once: the later excerpt points at the earlier one.</summary>
    [Fact]
    public void OverlappingExcerptsReferenceEarlierSegmentsInsteadOfCopyingThem()
    {
        var judge = new ScriptedJudge()
            .When("ships on", TaskKind.Check, "Check the date.", projectHint: "Atlas")
            .When("agreed", (seg, window) => new JudgeFinding(TaskKind.Check, 0.9, "email date agreed", "heard \"agreed\"", "Check the email date against the ship date.", window.Select(w => w.SegmentId).ToList(), "email", "Atlas"));
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Checked", [], "Consistent.", [], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: planner).WithWorkspace().WithListening()
            .StartListening()
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
        var judge = new ScriptedJudge()
            .When("agreed", (seg, window) => new JudgeFinding(TaskKind.Check, 0.9, "agreed", "heard \"agreed\"", "Check what was agreed.", window.Select(w => w.SegmentId).ToList(), null, "Atlas"));
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Checked", [], "Consistent.", [], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: planner).WithWorkspace().WithListening()
            .StartListening()
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

    /// <summary>When the model judge fails, the heuristic judge takes the pass and is labelled as such; nothing is skipped silently.</summary>
    [Fact]
    public void AFailingJudgeFallsBackToTheHeuristicJudgeVisibly()
    {
        var judge = new ScriptedJudge { Throws = new InvalidOperationException("llama.cpp is not running") };
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Checked", [], "Consistent.", [], [], "canned", Consistent: true));
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: new CompositeOrchestrator(new RuleBasedOrchestrator(), planner)).WithWorkspace()
            .Command("create project Atlas").Approve()
            .WithListening(JudgeSettings.Model).StartListening()
            .Hear(Decision).Observe()
            .ExpectEvent(EventTypes.ObserveFailed)
            .ExpectTask(TaskKind.Check, origin: TaskOrigin.Observed)
            .ExpectTask(TaskKind.Remember, TaskStatus.Completed, TaskOrigin.Observed)
            .ExpectEvent(EventTypes.NoteRouted)
            .ExpectExcerpts(1);

        Assert.Equal("llama.cpp is not running", s.Snap.Listening!.LastError);
        Assert.Equal("heuristic (fallback)", s.H.Last(EventTypes.ObserveFound)!.DataString("judge"));
        Assert.Equal("heuristic (fallback)", s.H.Last(EventTypes.TaskCreated)!.DataString("judge"));
        Assert.Equal("heuristic (fallback)", s.H.Excerpts.All()[0].SelectedBy);
        Assert.Equal("llama.cpp is not running", s.H.Last(EventTypes.ObserveFailed)!.DataString("error"));
        // Two findings on one sentence share one excerpt; the remembered note cites the words themselves.
        var excerpt = Assert.Single(s.H.Excerpts.All());
        Assert.Equal(excerpt.ExcerptId, s.FindTask(TaskKind.Check)!.ExcerptId);
        Assert.Equal(excerpt.ExcerptId, s.FindTask(TaskKind.Remember)!.ExcerptId);
        var atlas = s.H.Registry.FindActive("atlas")!;
        var note = Assert.Single(ProjectNoteStore.ReadAll(atlas.RootPath).Notes).Note;
        Assert.Equal(Decision, excerpt.Text[note.Spans[0].Start..note.Spans[0].End]);

        // The model comes back: the next pass is its own again and the error clears.
        judge.Throws = null;
        s.Hear(Chatter).Observe();
        Assert.Null(s.Snap.Listening!.LastError);
        Assert.Equal("scripted", s.H.Last(EventTypes.ObserveChecked)!.DataString("judge"));
    }

    [Fact]
    public void AJudgeThatNeverAnswersIsTimedOutAndTheStreamGoesOn()
    {
        var judge = new HangingJudge();
        using var s = Scenario.New(_tmp, judge: judge, configure: x => x.Judge.TimeoutMs = 3_000).WithWorkspace().WithListening(JudgeSettings.Model)
            .StartListening()
            .Hear(Chatter)
            .Silence(TimeSpan.FromSeconds(8))
            .ExpectEvent(EventTypes.ObserveFailed)
            .ExpectListening();

        Assert.Equal("timed out after 3000 ms", s.H.Last(EventTypes.ObserveFailed)!.DataString("error"));
        Assert.False(s.Snap.Listening!.Judging);
        Assert.True(judge.Calls >= 1);
        s.Hear(LaunchEmail).Silence(TimeSpan.FromSeconds(8));
        Assert.True(s.Snap.Listening!.JudgePasses >= 2, "the stream keeps judging after a timeout");
        s.StopListening().ExpectState(RelayState.Completed).ExpectListening(false);
    }

    /// <summary>A watched term is resolved the moment it is heard, pinned, and refreshed in place on every later mention.</summary>
    [Fact]
    public void AWatchedTermIsResolvedAtOnceAndPinned()
    {
        var judge = new ScriptedJudge().When("SLA", TaskKind.Resolve, "Define SLA as used here.", topic: "SLA", mergeKey: "define:sla");
        var planner = new CannedOrchestrator().Otherwise((_, _) => new TurnPlan(true, "Defined SLA", [], "SLA: service level agreement — the uptime and response commitments in the Atlas contract.", [], [], "canned"));
        using var s = Scenario.New(_tmp, judge: judge, orchestrator: planner).WithWorkspace()
            .Do("Always show SLA", c => Assert.True(c.UpdatePreference("display.alwaysShow", "SLA")))
            .ExpectPreference("display.alwaysShow", "SLA")
            .ExpectEvent(EventTypes.ChangeSetApplied)
            .WithListening().StartListening()
            .Hear("Our SLA promises four nines this quarter.");

        // No observe interval was waited for: the watched term triggered the judge immediately.
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
        using var s = Scenario.New(_tmp, judge: new ScriptedJudge()).WithWorkspace()
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
        using var s = Scenario.New(_tmp, judge: new ScriptedJudge()).WithWorkspace().WithListening()
            .StartListening()
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

    /// <summary>With the judge off the note chord is what it always was: dictation, organized when it settles.</summary>
    [Fact]
    public void WithTheJudgeOffTheNoteChordDictates()
    {
        using var s = Scenario.New(_tmp).WithWorkspace()
            .Command("create project Atlas").Approve()
            .StartListening();
        Assert.Null(s.Snap.Listening);
        Assert.False(s.Snap.ListeningEnabled);
        Assert.Equal("off", s.Snap.JudgeName);
        s.Do("dictate", c => c.TextChanged(Decision)).StopListening()
            .ExpectEvent(EventTypes.NoteRouted)
            .ExpectNoEvent(EventTypes.StreamStarted);
    }

    public void Dispose() => _tmp.Dispose();
}
