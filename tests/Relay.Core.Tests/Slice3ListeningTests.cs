using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Policy;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Slice 3: listening via the same CaseRuntime — segments persist, raise_task, concurrent direct asks,
/// acronyms, and corrections.
/// </summary>
public class Slice3ListeningTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 16, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task Segments_persist_and_survive_restart()
    {
        _tmp.Root.EnsureLayout(_clock);
        string caseId;
        string segmentId;
        string objectId;

        {
            using var diagnostics = Diag("s3-restart-a");
            var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

            var listening = runtime.StartListening();
            caseId = listening.Id;
            var seg = runtime.IngestSegment("API means Application Programming Interface", _clock.UtcNow, "alice");
            segmentId = seg.SegmentId;
            objectId = seg.ObjectId;

            Assert.True(File.Exists(Path.Combine(_tmp.Root.ObjectsDirectory, "by-id", objectId + ".json")));
            var events = runtime.Cases.LoadEvents(caseId);
            Assert.Contains(events, e => e.Type == CaseEventTypes.SegmentIngested);
            Assert.All(events.Where(e => e.Type == CaseEventTypes.SegmentIngested), e =>
            {
                Assert.True(e.Payload.TryGetProperty("objectId", out _));
                Assert.False(e.Payload.TryGetProperty("text", out _), "raw transcript must not sit in case event payload");
            });

            await runtime.StepCaseAsync(caseId);
            runtime.SuspendAll();
            Assert.Equal(CaseStatus.Suspended, runtime.GetCase(caseId)!.Status);
            Assert.True(runtime.Intake.LoadState().Active);
            Assert.Contains(segmentId, runtime.Intake.LoadState().IngestedSegmentIds);
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        {
            using var diagnostics = Diag("s3-restart-b");
            var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

            var resumed = runtime.GetListeningCase();
            Assert.NotNull(resumed);
            Assert.Equal(caseId, resumed!.Id);
            Assert.NotEqual(CaseStatus.Suspended, resumed.Status);

            var views = runtime.Intake.LoadRecentSegments(caseId);
            Assert.Contains(views, v => v.SegmentId == segmentId && v.Text.Contains("Application Programming Interface"));
            Assert.Equal(objectId, views.First(v => v.SegmentId == segmentId).ObjectId);
        }
    }

    [Fact]
    public async Task Raise_task_while_direct_case_also_active()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s3-raise");
        var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        var direct = runtime.StartDirectCase("What is on my plate?");
        var listening = runtime.StartListening();
        runtime.IngestSegment("We should schedule a design review next week.", _clock.UtcNow);

        var afterListen = await runtime.RunUntilIdleAsync(listening.Id);
        Assert.Equal(CaseOrigin.Observed, afterListen.Origin);
        Assert.NotEmpty(afterListen.ChildCaseIds);

        var childId = afterListen.ChildCaseIds[0];
        var child = runtime.GetCase(childId)!;
        Assert.Equal(CaseOrigin.Direct, child.Origin);
        Assert.Equal(listening.Id, child.ParentCaseId);
        Assert.Contains(child.SourceRefs, r => r.StartsWith("segment:", StringComparison.Ordinal));

        // Direct case still independently runnable.
        var answered = await runtime.RunUntilIdleAsync(direct.Id);
        Assert.Equal(CaseStatus.Completed, answered.Status);
        Assert.Contains("What is on my plate?", answered.Result ?? "");

        // Listening case still alive.
        Assert.Equal(CaseStatus.Active, runtime.GetCase(listening.Id)!.Status);
    }

    [Fact]
    public async Task Acronym_is_persistent_feed_without_stopping_capture()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s3-acronym");
        var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        var listening = runtime.StartListening();
        runtime.IngestSegment("API means Application Programming Interface", _clock.UtcNow);
        await runtime.RunUntilIdleAsync(listening.Id);

        var feed = runtime.Projections.ListFeedItems(listening.Id);
        Assert.Contains(feed, f => f.Level == "persistent" && f.Text.Contains("API means", StringComparison.Ordinal));

        // Capture continues: another segment still ingests.
        runtime.IngestSegment("Okay, moving on.", _clock.UtcNow.AddSeconds(5));
        Assert.Equal(2, runtime.Intake.LoadState().IngestedSegmentIds.Count);
        Assert.Equal(CaseStatus.Active, runtime.GetCase(listening.Id)!.Status);
    }

    [Fact]
    public async Task Correction_proposes_modify_note_not_duplicate()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, note) = local.SeedAtlasBetaDecision();

        using var diagnostics = Diag("s3-correct");
        var listenMind = new ListeningScriptedMind(project.Id, note.Id);
        var mind = new OriginRoutingMind(listenMind, new SimpleDirectMind());
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics, local: local);

        var listening = runtime.StartListening();
        runtime.IngestSegment("Actually, the Atlas beta ships on October 21.", _clock.UtcNow);
        var after = await runtime.RunUntilIdleAsync(listening.Id);
        Assert.Equal(CaseStatus.Waiting, after.Status);

        var pending = runtime.GetPendingApproval(listening.Id);
        Assert.NotNull(pending);
        Assert.Equal(Actions.ModifyNote, pending!.Capability);
        Assert.Equal(note.Id, pending.Arguments["noteId"].GetString());
        Assert.Contains("October 21", pending.Arguments["body"].GetString()!);

        // Direct ask still works while observed proposal remains pending.
        var direct = runtime.StartDirectCase("Quick status?");
        var answered = await runtime.RunUntilIdleAsync(direct.Id);
        Assert.Equal(CaseStatus.Completed, answered.Status);
        Assert.Equal(OperationStatus.AwaitingApproval, runtime.GetOperation(pending.OperationId)!.Status);
        Assert.Equal(CaseStatus.Waiting, runtime.GetCase(listening.Id)!.Status);
    }

    [Fact]
    public async Task StepNext_serves_direct_while_observed_waits_on_approval()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, note) = local.SeedAtlasBetaDecision();

        using var diagnostics = Diag("s3-concurrent");
        var mind = new OriginRoutingMind(new ListeningScriptedMind(project.Id, note.Id), new SimpleDirectMind());
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics, local: local);

        runtime.StartListening();
        runtime.IngestSegment("Correction: the Atlas beta ships on November 1.", _clock.UtcNow);
        var listenId = runtime.GetListeningCase()!.Id;
        await runtime.RunUntilIdleAsync(listenId);
        Assert.NotNull(runtime.GetPendingApproval(listenId));

        runtime.StartDirectCase("Ping from composer");
        // Drain ready queue: should be able to complete the direct case via StepNext.
        CaseRecord? lastDirect = null;
        for (var i = 0; i < 8; i++)
        {
            var stepped = await runtime.StepNextAsync();
            if (stepped is null) break;
            if (stepped.Origin == CaseOrigin.Direct)
                lastDirect = stepped;
        }

        Assert.NotNull(lastDirect);
        Assert.Equal(CaseStatus.Completed, lastDirect!.Status);
        Assert.Equal(CaseStatus.Waiting, runtime.GetCase(listenId)!.Status);
    }

    private RuntimeDiagnostics Diag(string runId)
        => new(Path.Combine(_tmp.Root.DevRunsDirectory, runId, "runtime.jsonl"), runId);

    public void Dispose() => _tmp.Dispose();
}
