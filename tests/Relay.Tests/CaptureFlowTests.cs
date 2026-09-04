using Relay.Core.Config;
using Relay.Core.Ledger;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Tests.Support;

namespace Relay.Tests;

public class CaptureFlowTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Fact]
    public void StartsIdleWithVerifiedLedgerAndSessionRecord()
    {
        using var h = new Harness(_tmp.Root).Start();
        Assert.Equal(RelayState.Idle, h.Snap.State);
        Assert.NotNull(h.Last(EventTypes.SessionStarted));
        Assert.NotNull(h.Last(EventTypes.LedgerVerified));
        Assert.NotNull(h.Last(EventTypes.SettingsLoaded));
        Assert.Equal("STARTING", h.Last(EventTypes.StateChanged)!.DataString("from"));
        Assert.Equal("IDLE", h.Last(EventTypes.StateChanged)!.DataString("to"));
        Assert.True(File.Exists(h.Sessions.PathFor(h.Coordinator.SessionId)));
        Assert.True(h.SettingsLoad.CreatedDefault);
        Assert.Equal("F13", h.Snap.NoteKey.Chord);
        Assert.Equal("F14", h.Snap.CommandKey.Chord);
    }

    [Fact]
    public void NoteCaptureStoresVerbatimTextAndADraftNote()
    {
        using var h = new Harness(_tmp.Root).Start();

        h.Coordinator.PressNoteKey();
        Assert.Equal(RelayState.NoteCapture, h.Snap.State);
        Assert.Equal(CaptureMode.Note, h.Snap.Mode);
        Assert.Equal(1, h.Host.PrepareCalls);
        Assert.True(File.Exists(h.Root.CurrentDraftPath));
        var started = h.Last(EventTypes.CaptureStarted)!;
        Assert.Equal("note", started.DataString("mode"));
        Assert.Equal("notepad", started.DataString("previousForegroundProcess"));

        const string text = "Idea: the ledger should be the only source of truth.\nSecond line, verbatim.";
        h.Coordinator.TextChanged(text);
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(250)); // draft debounce
        Assert.Contains(text, File.ReadAllText(h.Root.CurrentDraftPath).Replace("\\n", "\n"));

        h.Coordinator.PressNoteKey();
        Assert.Equal(RelayState.AwaitingTranscript, h.Snap.State);
        Assert.Equal(text.Length, h.Last(EventTypes.CaptureStopRequested)!.DataInt64("chars"));

        h.Scheduler.Advance(TimeSpan.FromMilliseconds(600)); // stabilization without relay
        Assert.Equal(RelayState.Completed, h.Snap.State);
        Assert.Contains("Saved 1 draft note", h.Snap.Receipt);

        var committed = h.Last(EventTypes.CaptureCommitted)!;
        Assert.Equal(text, committed.DataString("text"));
        Assert.Equal(text.Length, committed.DataInt64("chars"));
        Assert.Equal("note", committed.DataString("mode"));

        var note = h.Last(EventTypes.NoteDraftCreated)!;
        Assert.Equal(committed.Id, note.DataString("sourceEventId"));
        var notePath = note.DataString("path")!;
        Assert.True(File.Exists(notePath));
        Assert.Contains(committed.Id, File.ReadAllText(notePath));
        Assert.False(File.Exists(h.Root.CurrentDraftPath));

        // No conversational response of any kind in note mode.
        Assert.Null(h.Last(EventTypes.CommandRecorded));
        Assert.DoesNotContain(h.Snap.Review, r => r.Kind == ReviewItemKind.RecordedInstruction);

        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(RelayState.Idle, h.Snap.State);
    }

    [Fact]
    public void CommandCaptureRecordsTheInstructionAndRunsNothing()
    {
        using var h = new Harness(_tmp.Root, configure: s => s.Orchestrator.Mode = OrchestratorSettings.Off).Start();
        h.Coordinator.PressCommandKey();
        Assert.Equal(RelayState.CommandCapture, h.Snap.State);
        h.Coordinator.TextChanged("Create a project called market study");
        h.Coordinator.PressCommandKey();
        h.Scheduler.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(RelayState.Completed, h.Snap.State);
        var recorded = h.Last(EventTypes.CommandRecorded)!;
        Assert.Equal(false, recorded.DataBool("executed"));
        Assert.Equal("Create a project called market study", h.Last(EventTypes.CaptureCommitted)!.DataString("text"));
        Assert.Null(h.Last(EventTypes.NoteDraftCreated));
        var review = Assert.Single(h.Snap.Review, r => r.Kind == ReviewItemKind.RecordedInstruction);
        Assert.Equal("Create a project called market study", review.Payload);
        Assert.Empty(Directory.GetFiles(h.Root.DraftNotesDirectory));
    }

    [Fact]
    public void TheOtherKeyDuringCaptureIsRejectedAndLogged()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.TextChanged("half a thought");
        h.Coordinator.PressCommandKey();

        Assert.Equal(RelayState.NoteCapture, h.Snap.State);
        Assert.Equal(TransitionTable.FinishOrCancelFirst, h.Snap.Notice);
        var rejected = h.Last(EventTypes.HotkeyRejected)!;
        Assert.Equal("COMMAND_KEY", rejected.DataString("key"));
        Assert.Equal("NOTE_CAPTURE", rejected.DataString("state"));
        Assert.Null(h.Last(EventTypes.CaptureCommitted));
        Assert.Equal("half a thought".Length, h.Snap.CaptureChars);
    }

    [Fact]
    public void CancelStoresMetadataOnlyAndOffersRecovery()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.TextChanged("secret thing I did not mean to say");
        h.Coordinator.Cancel();

        Assert.Equal(RelayState.Idle, h.Snap.State);
        var cancelled = h.Last(EventTypes.CaptureCancelled)!;
        Assert.Equal(34, cancelled.DataInt64("chars"));
        Assert.Equal("NOTE_CAPTURE", cancelled.DataString("stateAtCancel"));
        Assert.DoesNotContain("secret", h.LedgerText());
        Assert.False(File.Exists(h.Root.CurrentDraftPath));
        Assert.Null(h.Last(EventTypes.CaptureCommitted));

        var review = Assert.Single(h.Snap.Review, r => r.Kind == ReviewItemKind.CancelledDraft);
        Assert.Equal("secret thing I did not mean to say", review.Payload);

        h.Coordinator.RecoverCancelledDraft();
        Assert.Equal(RelayState.Completed, h.Snap.State);
        Assert.Equal("secret thing I did not mean to say", h.Last(EventTypes.CaptureCommitted)!.DataString("text"));
        Assert.Equal(cancelled.DataString("captureId"), h.Last(EventTypes.CaptureDraftRecovered)!.DataString("originalCaptureId"));
        Assert.DoesNotContain(h.Snap.Review, r => r.Kind == ReviewItemKind.CancelledDraft);
    }

    [Fact]
    public void CancelDuringAwaitingTranscriptStopsTimers()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.PressNoteKey();
        Assert.Equal(RelayState.AwaitingTranscript, h.Snap.State);
        h.Coordinator.Cancel();
        Assert.Equal(RelayState.Idle, h.Snap.State);
        h.Scheduler.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(RelayState.Idle, h.Snap.State);
        Assert.Null(h.Last(EventTypes.CaptureTranscriptTimeout));
        Assert.DoesNotContain(h.Snap.Review, r => r.Kind == ReviewItemKind.CancelledDraft); // nothing to recover
    }

    [Fact]
    public void TimeoutIsVisibleAndLateTextStillCommits()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.PressNoteKey();

        h.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(RelayState.AwaitingTranscript, h.Snap.State);
        Assert.True(h.Snap.Awaiting!.TimedOut);
        Assert.True(h.Snap.CanRetryWait);
        Assert.Contains("does not read the clipboard", h.Snap.Notice);
        Assert.NotNull(h.Last(EventTypes.CaptureTranscriptTimeout));

        h.Coordinator.RetryWait();
        Assert.False(h.Snap.Awaiting!.TimedOut);
        Assert.Equal(1, h.Snap.Awaiting.Extensions);

        h.Coordinator.TextChanged("late but present");
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(700));
        Assert.Equal(RelayState.Completed, h.Snap.State);
        Assert.Equal("late but present", h.Last(EventTypes.CaptureCommitted)!.DataString("text"));
    }

    [Fact]
    public void SubmitNowShortCircuitsTheWait()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressCommandKey();
        h.Coordinator.TextChanged("do the thing");
        h.Coordinator.PressCommandKey();
        Assert.True(h.Snap.CanSubmitNow);
        h.Coordinator.SubmitNow();
        Assert.Equal(RelayState.Completed, h.Snap.State);
        Assert.NotNull(h.Last(EventTypes.CaptureSubmittedEarly));
    }

    [Fact]
    public void TextKeepsArrivingUntilTheTimeoutThenCommits()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.PressNoteKey();
        for (var i = 0; i < 40; i++)
        {
            h.Coordinator.TextChanged(new string('x', i + 1));
            h.Scheduler.Advance(TimeSpan.FromMilliseconds(250)); // always inside the 600ms quiet window
        }
        Assert.Equal(RelayState.Completed, h.Snap.State);
        Assert.Equal(40, h.Last(EventTypes.CaptureCommitted)!.DataInt64("chars"));
    }

    [Fact]
    public void FocusChangesAreRecordedOnlyDuringCapture()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.FocusChanged(true);
        h.Coordinator.FocusChanged(false);
        Assert.Null(h.Last(EventTypes.CaptureFocusLost));

        h.Coordinator.PressNoteKey();
        Assert.True(h.Snap.CaptureSurfaceFocused);
        h.Coordinator.FocusChanged(false);
        Assert.False(h.Snap.CaptureSurfaceFocused);
        Assert.NotNull(h.Last(EventTypes.CaptureFocusLost));
        h.Coordinator.FocusChanged(true);
        Assert.NotNull(h.Last(EventTypes.CaptureFocusRegained));
    }

    [Fact]
    public void RelaySendsStartAndStopOnlyWhenForeground()
    {
        using var h = new Harness(_tmp.Root, relayEnabled: true, configure: s => s.FlowRelay.Enabled = true).Start();
        h.Coordinator.PressNoteKey();
        Assert.Empty(h.Relay.Sent); // waits for the start delay
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal([RelayPurpose.Start], h.Relay.Sent);
        Assert.Equal("start", h.Last(EventTypes.FlowRelaySent)!.DataString("purpose"));
        Assert.Equal("Ctrl+Win+F24", h.Last(EventTypes.FlowRelaySent)!.DataString("chord"));

        h.Coordinator.TextChanged("dictated words");
        h.Host.Foreground = false;
        h.Coordinator.PressNoteKey();
        Assert.Equal([RelayPurpose.Start], h.Relay.Sent);
        Assert.Equal("stop", h.Last(EventTypes.FlowRelaySkipped)!.DataString("purpose"));
        Assert.Contains("not foreground", h.Last(EventTypes.FlowRelaySkipped)!.DataString("reason"));

        h.Scheduler.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(RelayState.Completed, h.Snap.State);
    }

    [Fact]
    public void RelayStartIsSkippedWhenTextAlreadyPresent()
    {
        using var h = new Harness(_tmp.Root, relayEnabled: true, configure: s => s.FlowRelay.Enabled = true).Start();
        h.Coordinator.PressNoteKey();
        h.Coordinator.TextChanged("typed before Flow started");
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(h.Relay.Sent);
        Assert.Contains("not empty", h.Last(EventTypes.FlowRelaySkipped)!.DataString("reason"));
    }

    [Fact]
    public void CancelWhileFlowIsListeningSendsStop()
    {
        using var h = new Harness(_tmp.Root, relayEnabled: true, configure: s => s.FlowRelay.Enabled = true).Start();
        h.Coordinator.PressCommandKey();
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(200));
        h.Coordinator.Cancel();
        Assert.Equal([RelayPurpose.Start, RelayPurpose.Stop], h.Relay.Sent);
    }

    [Fact]
    public void KeysAreIgnoredAfterShutdown()
    {
        using var h = new Harness(_tmp.Root).Start();
        h.Coordinator.Shutdown("user_exit");
        h.Coordinator.PressNoteKey();
        Assert.Equal(RelayState.Idle, h.Snap.State);
        Assert.Equal("user_exit", h.Last(EventTypes.SessionEnded)!.DataString("reason"));
    }

    public void Dispose() => _tmp.Dispose();
}
