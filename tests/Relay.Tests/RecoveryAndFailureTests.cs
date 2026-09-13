using Relay.Core.Ledger;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Tests.Support;

namespace Relay.Tests;

public class RecoveryAndFailureTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Fact]
    public void CrashDuringCaptureIsDetectedAndTheDraftIsRecoverable()
    {
        string captureId;
        string firstSession;
        using (var h1 = new Harness(_tmp.Root).Start())
        {
            h1.Coordinator.PressNoteKey();
            h1.Coordinator.TextChanged("words that must survive a crash");
            h1.Scheduler.Advance(TimeSpan.FromMilliseconds(250));
            captureId = h1.Snap.CaptureId!;
            firstSession = h1.Coordinator.SessionId;
            h1.Crash();
        }

        using var h2 = new Harness(_tmp.Root, clock: new FixedClock(Harness.T0.AddMinutes(5))).Start();
        Assert.Equal(RelayState.Ready, h2.Snap.State);
        Assert.Single(h2.Recovery.CrashedSessions);
        Assert.Equal(firstSession, h2.Last(EventTypes.SessionCrashDetected)!.DataString("crashedSessionId"));

        var found = h2.Last(EventTypes.CaptureInterruptedFound)!;
        Assert.Equal(captureId, found.DataString("captureId"));
        Assert.Equal(false, found.DataBool("alreadyResolved"));
        var item = Assert.Single(h2.Snap.Review, r => r.Kind == ReviewItemKind.InterruptedCapture);
        Assert.Equal("words that must survive a crash", item.Payload);

        h2.Coordinator.CommitInterrupted();
        Assert.Equal(RelayState.Ready, h2.Snap.State);
        var committed = h2.Last(EventTypes.CaptureCommitted)!;
        Assert.Equal(captureId, committed.DataString("captureId"));
        Assert.Equal("words that must survive a crash", committed.DataString("text"));
        Assert.False(File.Exists(h2.Root.CurrentDraftPath));
        Assert.Equal("CommitInterrupted", h2.Records().First(r => r.Type == EventTypes.StateChanged && r.DataString("to") == "ORGANIZING").DataString("trigger"));

        // The crashed session file is closed out by recovery, so a third start reports nothing new.
        h2.CleanExit();
        using var h3 = new Harness(_tmp.Root, clock: new FixedClock(Harness.T0.AddMinutes(10))).Start();
        Assert.Empty(h3.Recovery.CrashedSessions);
        Assert.Null(h3.Recovery.InterruptedDraft);
    }

    [Fact]
    public void InterruptedDraftCanBeDiscardedWithoutDeletingIt()
    {
        using (var h1 = new Harness(_tmp.Root).Start())
        {
            h1.Coordinator.PressCommandKey();
            h1.Coordinator.TextChanged("half an instruction");
            h1.Scheduler.Advance(TimeSpan.FromMilliseconds(250));
            h1.Crash();
        }
        using var h2 = new Harness(_tmp.Root).Start();
        var id = h2.Recovery.InterruptedDraft!.CaptureId;
        h2.Coordinator.DiscardInterrupted();
        Assert.DoesNotContain(h2.Snap.Review, r => r.Kind == ReviewItemKind.InterruptedCapture);
        var discarded = h2.Last(EventTypes.CaptureInterruptedDiscarded)!;
        Assert.Equal(id, discarded.DataString("captureId"));
        Assert.True(File.Exists(Path.Combine(h2.Root.DiscardedDraftsDirectory, id + ".json")));
        Assert.False(File.Exists(h2.Root.CurrentDraftPath));
        Assert.Null(h2.Last(EventTypes.CaptureCommitted));
    }

    [Fact]
    public void StaleDraftForAnAlreadyCommittedCaptureIsRemovedSilently()
    {
        string committedId;
        using (var h1 = new Harness(_tmp.Root).Start())
        {
            h1.Coordinator.PressNoteKey();
            h1.Coordinator.TextChanged("committed words");
            h1.Coordinator.PressNoteKey();
            h1.Scheduler.Advance(TimeSpan.FromSeconds(1));
            committedId = h1.Last(EventTypes.CaptureCommitted)!.DataString("captureId")!;
            // Simulate a crash between the ledger append and the staging cleanup.
            h1.Drafts.Write(new Relay.Core.Captures.CaptureDraft
            {
                CaptureId = committedId, ModeWire = "note", SessionId = h1.Coordinator.SessionId,
                StartedAt = Harness.T0, UpdatedAt = Harness.T0, Text = "committed words",
            });
            h1.Crash();
        }
        using var h2 = new Harness(_tmp.Root).Start();
        Assert.True(h2.Recovery.InterruptedDraftAlreadyResolved);
        Assert.Equal(true, h2.Last(EventTypes.CaptureInterruptedFound)!.DataBool("alreadyResolved"));
        Assert.DoesNotContain(h2.Snap.Review, r => r.Kind == ReviewItemKind.InterruptedCapture);
        Assert.False(File.Exists(h2.Root.CurrentDraftPath));
    }

    [Fact]
    public void CleanExitDuringCaptureKeepsTheDraftWithoutReportingACrash()
    {
        using (var h1 = new Harness(_tmp.Root).Start())
        {
            h1.Coordinator.PressNoteKey();
            h1.Coordinator.TextChanged("closing the app mid-note");
            h1.CleanExit("user_exit");
            Assert.Equal(h1.Snap.CaptureId, h1.Last(EventTypes.SessionEnded)!.DataString("activeCaptureId"));
        }
        using var h2 = new Harness(_tmp.Root).Start();
        Assert.Empty(h2.Recovery.CrashedSessions);
        Assert.NotNull(h2.Recovery.InterruptedDraft);
        Assert.Equal("closing the app mid-note", h2.Recovery.InterruptedDraft!.Text);
    }

    [Fact]
    public void TornLedgerTailIsRepairedAndRecorded()
    {
        using (var h1 = new Harness(_tmp.Root).Start()) h1.CleanExit();
        var bytes = File.ReadAllBytes(_tmp.Root.LedgerPath);
        var fragment = "{\"seq\":99,\"id\":\"partial"u8.ToArray();
        File.WriteAllBytes(_tmp.Root.LedgerPath, [.. bytes, .. fragment]);

        using var h2 = new Harness(_tmp.Root).Start();
        Assert.Equal(RelayState.Ready, h2.Snap.State);
        Assert.NotNull(h2.Recovery.QuarantinePath);
        var repaired = h2.Last(EventTypes.LedgerRepaired)!;
        Assert.Equal(fragment.Length, repaired.DataInt64("quarantinedBytes"));
        Assert.Equal(fragment, File.ReadAllBytes(h2.Recovery.QuarantinePath!));
        Assert.Equal(LedgerHealth.Ok, LedgerVerifier.Verify(_tmp.Root.LedgerPath).Health);
        Assert.Single(Directory.GetFiles(_tmp.Root.LedgerQuarantineDirectory));
    }

    [Fact]
    public void TamperedLedgerLocksTheSystemUntilExplicitUnlock()
    {
        using (var h1 = new Harness(_tmp.Root).Start()) h1.CleanExit();
        var lines = File.ReadAllLines(_tmp.Root.LedgerPath);
        lines[1] = lines[1].Replace("\"type\":\"", "\"type\":\"x.");
        File.WriteAllLines(_tmp.Root.LedgerPath, lines);

        using var h2 = new Harness(_tmp.Root).Start();
        Assert.Equal(RelayState.Locked, h2.Snap.State);
        Assert.Equal("ledger_integrity_failed", h2.Snap.Incident!.Kind);
        Assert.Equal(2, h2.Last(EventTypes.LedgerIntegrityFailed)!.DataInt64("brokenSeq"));
        Assert.NotNull(h2.Last(EventTypes.LockEngaged));
        Assert.Contains(h2.Snap.Review, r => r.Kind == ReviewItemKind.Incident);
        Assert.Single(Directory.GetFiles(_tmp.Root.IncidentsDirectory));

        h2.Coordinator.PressNoteKey();
        Assert.Equal(RelayState.Locked, h2.Snap.State);
        Assert.Equal(TransitionTable.LockedMessage, h2.Snap.Notice);
        Assert.Equal("LOCKED", h2.Last(EventTypes.HotkeyRejected)!.DataString("state"));

        h2.Coordinator.Unlock();
        Assert.Equal(RelayState.Ready, h2.Snap.State);
        Assert.Null(h2.Snap.Incident);
        Assert.Equal("ledger_integrity_failed", h2.Last(EventTypes.LockReleased)!.DataString("acknowledged"));

        // New records chain from the tampered tail, so the break remains evident forever.
        var v = LedgerVerifier.Verify(_tmp.Root.LedgerPath);
        Assert.Equal(LedgerHealth.IntegrityFailure, v.Health);
        Assert.Equal(2, v.BrokenSeq);
    }

    [Fact]
    public void LedgerWriteFailureLocksAndPreservesTheDraft()
    {
        // Allow startup records and the capture start, then fail on the next append.
        using var h = new Harness(_tmp.Root).Start();
        h.Faulty.FailAfterAppends = h.Faulty.Appends + 2; // capture.started + state.changed succeed
        h.Coordinator.PressNoteKey();
        Assert.Equal(RelayState.Ready /*was NoteCapture*/, h.Snap.State);
        h.Coordinator.TextChanged("text during a disk failure");
        h.Coordinator.PressNoteKey(); // state.changed or capture.stop_requested fails → LOCKED

        Assert.Equal(RelayState.Locked, h.Snap.State);
        Assert.Equal("ledger_write_failed", h.Snap.Incident!.Kind);
        Assert.NotNull(h.Snap.Incident.IncidentFilePath);
        Assert.True(File.Exists(h.Snap.Incident.IncidentFilePath));
        Assert.True(File.Exists(h.Root.CurrentDraftPath));
        Assert.Contains("text during a disk failure", File.ReadAllText(h.Root.CurrentDraftPath));

        h.Coordinator.Unlock();
        Assert.Equal(RelayState.Locked, h.Snap.State); // still failing

        h.Faulty.FailAfterAppends = int.MaxValue;
        h.Coordinator.Unlock();
        Assert.Equal(RelayState.Ready, h.Snap.State);
        Assert.NotNull(h.Last(EventTypes.LockReleased));
    }

    [Fact]
    public void ReportedFailureDuringCaptureBecomesAnInterruptedCaptureOnDismiss()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.TextChanged("in flight when the UI threw");
        h.Coordinator.ReportFailure(new InvalidOperationException("boom"), "MainWindow.Render");

        Assert.Equal(RelayState.Failed, h.Snap.State);
        Assert.Equal("app_failed", h.Snap.Incident!.Kind);
        Assert.NotNull(h.Last(EventTypes.AppFailed));
        Assert.False(h.Snap.CanRetry);
        Assert.True(File.Exists(h.Root.CurrentDraftPath));

        h.Coordinator.Dismiss();
        Assert.Equal(RelayState.Ready, h.Snap.State);
        var item = Assert.Single(h.Snap.Review, r => r.Kind == ReviewItemKind.InterruptedCapture);
        Assert.Equal("in flight when the UI threw", item.Payload);
        h.Coordinator.CommitInterrupted();
        Assert.Equal("in flight when the UI threw", h.Last(EventTypes.CaptureCommitted)!.DataString("text"));
    }

    [Fact]
    public void OrganizeFailureCanBeRetriedWithoutDuplicatingTheCommit()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.TextChanged("note whose file write fails once");
        // Make the note directory unwritable by placing a file where the directory should be.
        Directory.Delete(h.Root.DraftNotesDirectory, recursive: true);
        File.WriteAllText(h.Root.DraftNotesDirectory, "blocker");
        h.Coordinator.PressNoteKey();
        h.Scheduler.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(RelayState.Failed, h.Snap.State);
        Assert.True(h.Snap.CanRetry);
        Assert.Equal(1, h.Count(EventTypes.CaptureCommitted));
        Assert.NotNull(h.Last(EventTypes.CaptureOrganizeFailed));

        File.Delete(h.Root.DraftNotesDirectory);
        h.Coordinator.Retry();
        Assert.Equal(RelayState.Ready, h.Snap.State);
        Assert.Equal(1, h.Count(EventTypes.CaptureCommitted)); // idempotent
        Assert.Equal(1, h.Count(EventTypes.NoteDraftCreated));
    }

    public void Dispose() => _tmp.Dispose();
}
