using Relay.Core.Cases;
using Relay.Core.Listening;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>§9 durable listening windows, coverage, and outage backlog.</summary>
public class ListeningWindowTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 21, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    private (ListeningController controller, ListeningWindowStore store) Controller()
    {
        _tmp.Root.EnsureLayout(_clock);
        var store = new ListeningWindowStore(_tmp.Root, _clock);
        return (new ListeningController(store, _clock), store);
    }

    [Fact]
    public void Coalesces_within_2s_and_splits_on_gap()
    {
        var (controller, _) = Controller();
        var segments = new List<SequencedSegment>
        {
            new("s0", 0, T0, "Hello one."),
            new("s1", 1, T0.AddSeconds(1), "Hello two."),
            new("s2", 2, T0.AddSeconds(5), "After gap."),
        };

        var windows = controller.FormWindows("sess-1", "case-1", segments);
        Assert.Equal(2, windows.Count);
        Assert.Equal(2, windows[0].Primary.SegmentIds.Count);
        Assert.Equal(["s0", "s1"], windows[0].Primary.SegmentIds);
        Assert.Equal(["s2"], windows[1].Primary.SegmentIds);
    }

    [Fact]
    public void Preceding_overlap_is_context_only()
    {
        var (controller, _) = Controller();
        var segments = new List<SequencedSegment>
        {
            new("old", 0, T0, "Earlier talk."),
            new("new", 1, T0.AddSeconds(10), "Primary talk."),
        };
        // Cover "old" first so second form uses it as preceding context.
        controller.FormWindows("sess-1", "case-1", [segments[0]]);
        var next = controller.FormWindows("sess-1", "case-1", segments);
        Assert.Single(next);
        Assert.Contains("new", next[0].Primary.SegmentIds);
        Assert.DoesNotContain("old", next[0].Primary.SegmentIds);
        Assert.Contains("old", next[0].Context.SegmentIds);
        Assert.Equal(CoverageRoles.Context, next[0].Context.Role);
    }

    [Fact]
    public void Caps_primary_at_8000_chars_and_splits_sentences()
    {
        var (controller, _) = Controller();
        var longText = string.Join(" ", Enumerable.Range(0, 500).Select(i => $"Sentence number {i} is here."));
        Assert.True(longText.Length > ListeningWindowPolicy.MaxWindowChars);
        var windows = controller.FormWindows("sess-1", "case-1",
            [new SequencedSegment("big", 0, T0, longText)]);
        Assert.True(windows.Count >= 2);
        Assert.All(windows, w => Assert.True(w.Primary.CharCount <= ListeningWindowPolicy.MaxWindowChars));
    }

    [Fact]
    public void MarkCovered_requires_explicit_segment_ids_no_first_pending_fallback()
    {
        var (controller, store) = Controller();
        var windows = controller.FormWindows("sess-1", "case-1",
            [new SequencedSegment("s0", 0, T0, "Talk.")]);
        var id = windows[0].WindowId;
        Assert.False(controller.TryMarkCoveredWithoutExplicitIds(id));
        Assert.False(controller.MarkCovered(id, []));
        Assert.Equal(ListeningWindowStatus.Pending, store.TryLoad(id)!.Status);
        Assert.True(controller.MarkCovered(id, ["s0"]));
        Assert.Equal(ListeningWindowStatus.Completed, store.TryLoad(id)!.Status);
    }

    [Fact]
    public void Prefer_finalized_utterances_over_interim()
    {
        var (controller, _) = Controller();
        var segments = new List<SequencedSegment>
        {
            new("interim", 0, T0, "Same text", "alice", Finalized: false),
            new("final", 1, T0.AddMilliseconds(200), "Same text", "alice", Finalized: true),
        };
        var windows = controller.FormWindows("sess-1", "case-1", segments);
        Assert.Single(windows);
        Assert.Equal(["final"], windows[0].Primary.SegmentIds);
    }

    [Fact]
    public async Task Capture_continues_while_windows_stay_pending_during_outage()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "s9-outage", "runtime.jsonl"), "s9-outage");
        var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        runtime.StartListening();
        for (var i = 0; i < 5; i++)
        {
            runtime.IngestSegment($"Segment {i}.", _clock.UtcNow.AddSeconds(i * 3));
        }

        var state = runtime.Intake.LoadState();
        Assert.True(state.CaptureEnabled);
        Assert.True(state.PendingWindowIds.Count >= 5);
        Assert.Equal(5, state.IngestedSegmentIds.Count);

        // Simulate outage: do not step; keep pending windows durable.
        var pending = runtime.Listening.PendingThroughOutage(state.SessionId!);
        Assert.True(pending.Count >= 5);

        // Capture continues.
        runtime.IngestSegment("After outage.", _clock.UtcNow.AddSeconds(20));
        Assert.Equal(6, runtime.Intake.LoadState().IngestedSegmentIds.Count);
        await Task.CompletedTask;
    }

    [Fact]
    public void Hundred_plus_queued_segments_survive_outage_and_restart_without_coverage_gaps()
    {
        _tmp.Root.EnsureLayout(_clock);
        string sessionId;
        string caseId;
        var segmentIds = new List<string>();

        {
            using var diagnostics = new RuntimeDiagnostics(
                Path.Combine(_tmp.Root.DevRunsDirectory, "s9-100-a", "runtime.jsonl"), "s9-100-a");
            var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

            var listening = runtime.StartListening();
            caseId = listening.Id;
            sessionId = runtime.Intake.LoadState().SessionId!;

            for (var i = 0; i < 120; i++)
            {
                // Gaps >2s so each segment becomes its own window (stress backlog size).
                var seg = runtime.IngestSegment($"Queued utterance number {i}.", _clock.UtcNow.AddSeconds(i * 3));
                segmentIds.Add(seg.SegmentId);
            }

            var state = runtime.Intake.LoadState();
            Assert.Equal(120, state.IngestedSegmentIds.Count);
            Assert.True(state.PendingWindowIds.Count >= 120);
            // Must NOT collapse to recent 32.
            Assert.True(runtime.Intake.LoadAllSegments(caseId).Count > ListeningWindowPolicy.ForbiddenRecentOnlyBacklogLimit);
            Assert.Equal(120, runtime.Intake.LoadAllSegments(caseId).Count);
            Assert.False(runtime.Listening.HasCoverageGaps(sessionId, segmentIds));
            runtime.SuspendAll();
        }

        _clock.Advance(TimeSpan.FromMinutes(5));
        {
            using var diagnostics = new RuntimeDiagnostics(
                Path.Combine(_tmp.Root.DevRunsDirectory, "s9-100-b", "runtime.jsonl"), "s9-100-b");
            var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

            var resumed = runtime.GetListeningCase();
            Assert.NotNull(resumed);
            Assert.Equal(caseId, resumed!.Id);

            var state = runtime.Intake.LoadState();
            Assert.True(state.Active);
            Assert.Equal(120, state.IngestedSegmentIds.Count);
            Assert.True(state.PendingWindowIds.Count >= 120);

            var all = runtime.Intake.LoadAllSegments(caseId);
            Assert.Equal(120, all.Count);
            Assert.Equal(32, runtime.Intake.LoadRecentSegments(caseId, 32).Count);

            var pending = runtime.Listening.PendingThroughOutage(sessionId);
            Assert.True(pending.Count >= 120);
            Assert.False(runtime.Listening.HasCoverageGaps(sessionId, segmentIds));
        }
    }

    [Fact]
    public void Session_tracks_capture_grant_sequence_windows_and_retention()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "s9-session", "runtime.jsonl"), "s9-session");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);

        runtime.StartListening();
        var state = runtime.Intake.LoadState();
        Assert.True(state.CaptureEnabled);
        Assert.Equal("transcript_30d", state.RetentionPolicy);
        Assert.NotNull(state.SessionId);
        Assert.Equal(0, state.NextSegmentSequence);

        runtime.IngestSegment("First.", _clock.UtcNow);
        state = runtime.Intake.LoadState();
        Assert.Equal(1, state.NextSegmentSequence);
        Assert.NotEmpty(state.PendingWindowIds);
        Assert.Null(state.HostedGrantId); // independent of capture
    }

    [Fact]
    public async Task StreamIntake_keeps_capture_without_semantic_keyword_processing()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "s9-capture", "runtime.jsonl"), "s9-capture");
        // Direct mind only — no listening keyword mind. Capture + windows still work.
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new SimpleDirectMind(), diagnostics);

        runtime.StartListening();
        var seg = runtime.IngestSegment("API means Application Programming Interface", _clock.UtcNow);
        Assert.False(string.IsNullOrEmpty(seg.SegmentId));
        var windows = runtime.ListeningWindows.ListBySession(runtime.Intake.LoadState().SessionId!);
        Assert.NotEmpty(windows);
        // No mind step → no feed acronym expansion; capture/windows only.
        Assert.DoesNotContain(runtime.Projections.ListFeedItems(runtime.GetListeningCase()!.Id),
            f => f.Text.Contains("API means", StringComparison.Ordinal));
        await Task.CompletedTask;
    }
}
