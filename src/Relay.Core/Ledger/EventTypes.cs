namespace Relay.Core.Ledger;

/// <summary>
/// Every record type the v0.1 slice can write. Names are stable identifiers: never rename one,
/// add a new one instead. The activity feed maps each to a plain-language line.
/// </summary>
public static class EventTypes
{
    // Session lifecycle
    public const string SessionStarted = "session.started";
    public const string SessionEnded = "session.ended";
    public const string SessionCrashDetected = "session.crash_detected";

    // Ledger self-checks
    public const string LedgerVerified = "ledger.verified";
    public const string LedgerRepaired = "ledger.repaired";
    public const string LedgerIntegrityFailed = "ledger.integrity_failed";

    // Configuration and storage
    public const string SettingsLoaded = "settings.loaded";
    public const string SettingsInvalid = "settings.invalid";
    public const string StorageAclApplied = "storage.acl_applied";
    public const string StorageAclFailed = "storage.acl_failed";

    // Hotkeys
    public const string HotkeyRegistered = "hotkey.registered";
    public const string HotkeyRegistrationFailed = "hotkey.registration_failed";
    public const string HotkeyRejected = "hotkey.rejected";

    // State machine
    public const string StateChanged = "state.changed";

    // Capture lifecycle
    public const string CaptureStarted = "capture.started";
    public const string CaptureFocusLost = "capture.focus_lost";
    public const string CaptureFocusRegained = "capture.focus_regained";
    public const string CaptureStopRequested = "capture.stop_requested";
    public const string CaptureTranscriptStable = "capture.transcript_stable";
    public const string CaptureTranscriptTimeout = "capture.transcript_timeout";
    public const string CaptureWaitExtended = "capture.wait_extended";
    public const string CaptureSubmittedEarly = "capture.submitted_early";
    public const string CaptureCommitted = "capture.committed";
    public const string CaptureCancelled = "capture.cancelled";
    public const string CaptureDraftRecovered = "capture.draft_recovered";
    public const string CaptureInterruptedFound = "capture.interrupted_found";
    public const string CaptureInterruptedDiscarded = "capture.interrupted_discarded";
    public const string CaptureOrganizeFailed = "capture.organize_failed";

    // Derived records
    public const string NoteDraftCreated = "note.draft_created";
    public const string CommandRecorded = "command.recorded";

    // Flow relay adapter
    public const string FlowRelaySent = "flow.relay_sent";
    public const string FlowRelaySkipped = "flow.relay_skipped";

    // Failure and protection
    public const string AppFailed = "app.failed";
    public const string LockEngaged = "lock.engaged";
    public const string LockReleased = "lock.released";
}
