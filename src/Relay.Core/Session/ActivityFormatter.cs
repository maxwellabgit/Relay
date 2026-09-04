using Relay.Core.Ledger;

namespace Relay.Core.Session;

/// <summary>
/// Turns ledger records into the plain-language activity lines the UI shows (contract §6).
/// Capture text is never included; the record holds it, the feed only describes it.
/// </summary>
public static class ActivityFormatter
{
    public static ActivityEntry Format(LedgerRecord r)
    {
        var text = r.Type switch
        {
            EventTypes.SessionStarted => $"Session started (pid {r.DataInt64("pid")}, Relay {r.DataString("appVersion")})",
            EventTypes.SessionEnded => $"Session ended: {r.DataString("reason")}",
            EventTypes.SessionCrashDetected => $"Previous session {Short(r.DataString("crashedSessionId"))} did not shut down cleanly",
            EventTypes.LedgerVerified => $"Ledger verified: {r.DataInt64("records")} records, chain intact",
            EventTypes.LedgerRepaired => $"Ledger repaired: {r.DataInt64("quarantinedBytes")} incomplete byte(s) quarantined",
            EventTypes.LedgerIntegrityFailed => $"Ledger integrity failure at record {r.DataInt64("brokenSeq")}: {r.DataString("reason")}",
            EventTypes.SettingsLoaded => r.DataBool("createdDefault") == true ? "Created default settings" : "Loaded settings",
            EventTypes.SettingsInvalid => $"Settings problem: {r.DataString("problem")}",
            EventTypes.StorageAclApplied => "Restricted data folder to this user and SYSTEM",
            EventTypes.StorageAclFailed => $"Could not restrict data folder permissions: {r.DataString("error")}",
            EventTypes.HotkeyRegistered => $"Registered {r.DataString("name")} as {r.DataString("chord")}",
            EventTypes.HotkeyRegistrationFailed => $"Could not register {r.DataString("name")} ({r.DataString("chord")}): {r.DataString("error")}",
            EventTypes.HotkeyRejected => $"{r.DataString("key")} ignored while {r.DataString("state")}: {r.DataString("reason")}",
            EventTypes.StateChanged => $"{r.DataString("from")} → {r.DataString("to")} ({r.DataString("trigger")})",
            EventTypes.CaptureStarted => $"Started {r.DataString("mode")} capture {Short(r.DataString("captureId"))}",
            EventTypes.CaptureFocusLost => "Capture surface lost focus; text will not arrive here",
            EventTypes.CaptureFocusRegained => "Capture surface regained focus",
            EventTypes.CaptureStopRequested => $"Stop requested with {r.DataInt64("chars")} character(s) present",
            EventTypes.CaptureTranscriptStable => $"Transcript stable at {r.DataInt64("chars")} character(s)",
            EventTypes.CaptureTranscriptTimeout => "No transcript arrived before the timeout",
            EventTypes.CaptureWaitExtended => "Waiting for the transcript again",
            EventTypes.CaptureSubmittedEarly => $"Submitted early with {r.DataInt64("chars")} character(s)",
            EventTypes.CaptureCommitted => $"Stored {r.DataString("mode")} capture {Short(r.DataString("captureId"))} ({r.DataInt64("chars")} chars, sha256 {Short(r.DataString("sha256"))})",
            EventTypes.CaptureCancelled => $"Cancelled {r.DataString("mode")} capture ({r.DataInt64("chars")} chars not stored)",
            EventTypes.CaptureDraftRecovered => $"Recovered cancelled draft as capture {Short(r.DataString("captureId"))}",
            EventTypes.CaptureInterruptedFound => $"Found interrupted {r.DataString("mode")} capture {Short(r.DataString("captureId"))} ({r.DataInt64("chars")} chars)",
            EventTypes.CaptureInterruptedDiscarded => $"Discarded interrupted capture {Short(r.DataString("captureId"))} to staging\\drafts\\discarded",
            EventTypes.CaptureOrganizeFailed => $"Could not store capture: {r.DataString("error")}",
            EventTypes.NoteDraftCreated => $"Created draft note {Short(r.DataString("noteId"))} from capture {Short(r.DataString("captureId"))}",
            EventTypes.CommandRecorded => $"Recorded instruction ({r.DataInt64("chars")} chars); orchestrator not enabled in this build",
            EventTypes.FlowRelaySent => $"Sent Flow {r.DataString("purpose")} chord {r.DataString("chord")}" + (r.DataBool("ok") == true ? "" : $" — failed: {r.DataString("error")}"),
            EventTypes.FlowRelaySkipped => $"Flow {r.DataString("purpose")} chord not sent: {r.DataString("reason")}",
            EventTypes.AppFailed => $"Failure in {r.DataString("where")}: {r.DataString("exceptionType")}: {r.DataString("message")}",
            EventTypes.LockEngaged => $"LOCKED: {r.DataString("reason")}",
            EventTypes.LockReleased => "Unlocked by user",
            _ => r.Type,
        };
        return new ActivityEntry(r.Seq, r.Timestamp, r.Type, text);
    }

    private static string Short(string? id) => id is null ? "?" : id.Length > 10 ? id[^8..] : id;
}
