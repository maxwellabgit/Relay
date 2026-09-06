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

    // Failure and protection
    public const string AppFailed = "app.failed";
    public const string LockEngaged = "lock.engaged";
    public const string LockReleased = "lock.released";

    // Workspaces and projects (phase 2)
    public const string WorkspaceRegistered = "workspace.registered";
    public const string WorkspaceRemoved = "workspace.removed";
    public const string ProjectCreated = "project.created";
    public const string ProjectRenamed = "project.renamed";
    public const string ProjectArchived = "project.archived";
    public const string ProjectRestored = "project.restored";
    public const string NoteRouted = "note.routed";
    public const string NoteWritten = "note.written";
    public const string NoteModified = "note.modified";
    public const string NoteExtracted = "note.extracted";
    public const string NoteRoutingDeferred = "note.routing_deferred";
    public const string NoteDisputed = "note.disputed";
    public const string NoteSuperseded = "note.superseded";
    public const string BackupExported = "backup.exported";
    public const string BackupVerified = "backup.verified";

    // Orchestrator turns (phase 3)
    public const string TurnStarted = "turn.started";
    public const string TurnCompleted = "turn.completed";
    public const string TurnCancelled = "turn.cancelled";
    public const string TurnFailed = "turn.failed";
    public const string TurnInterruptedFound = "turn.interrupted_found";
    public const string TurnProgress = "turn.progress";
    public const string PlanProposed = "plan.proposed";
    public const string ToolCalled = "tool.called";
    public const string ToolReturned = "tool.returned";
    public const string ModelRequested = "model.requested";
    public const string ModelResponded = "model.responded";

    // Proposals, approvals, execution (phase 4)
    public const string ProposalReceived = "proposal.received";
    public const string ProposalDecided = "proposal.decided";
    public const string ProposalEdited = "proposal.edited";
    public const string ApprovalGranted = "approval.granted";
    public const string ApprovalRejected = "approval.rejected";
    public const string ExecutionStarted = "execution.started";
    public const string ExecutionCompleted = "execution.completed";
    public const string ExecutionFailed = "execution.failed";
    public const string ExecutionStopRequested = "execution.stop_requested";
    public const string ExecutionInterruptedFound = "execution.interrupted_found";

    // Worker agents (phase 5)
    public const string AgentRunLaunched = "agent_run.launched";
    public const string AgentRunToolCalled = "agent_run.tool_called";
    public const string AgentRunToolDenied = "agent_run.tool_denied";
    public const string AgentRunLog = "agent_run.log";
    public const string AgentRunCompleted = "agent_run.completed";
    public const string AgentRunTerminated = "agent_run.terminated";
    public const string PatchApplied = "patch.applied";

    // Settings changed through the UI
    public const string SettingsChanged = "settings.changed";
    /// <summary>A protected secret was stored or removed; carries the secret's name only, never its value.</summary>
    public const string SecretChanged = "settings.secret_changed";

    // Streams (listening): metadata only, never text
    public const string StreamStarted = "stream.started";
    public const string StreamSegment = "stream.segment";
    public const string StreamStopped = "stream.stopped";
    /// <summary>A buffer window file survived a crash; it is discarded (it expired with the buffer) and only its size is recorded.</summary>
    public const string StreamInterruptedFound = "stream.interrupted_found";
    /// <summary>A judge pass that found nothing: segment ids, hashes, tokens, latency. No words.</summary>
    public const string ObserveChecked = "observe.checked";
    /// <summary>A judge pass that found something; the findings become tasks and excerpts.</summary>
    public const string ObserveFound = "observe.found";
    public const string ObserveFailed = "observe.failed";
    /// <summary>An excerpt was persisted: ids, seconds, chars, whether the guard shrank it.</summary>
    public const string ExcerptStored = "stream.excerpt_stored";
    /// <summary>The user asked something directly; the instruction text is kept (it is intent, not conversation).</summary>
    public const string AskRecorded = "ask.recorded";

    // Tasks (one pipeline, several origins)
    public const string TaskCreated = "task.created";
    public const string TaskPlanned = "task.planned";
    public const string TaskPresented = "task.presented";
    public const string TaskMerged = "task.merged";
    public const string TaskCompleted = "task.completed";
    public const string TaskFailed = "task.failed";
    public const string TaskCancelled = "task.cancelled";
    public const string TaskUserResponse = "task.user_response";
    public const string TaskInterruptedFound = "task.interrupted_found";
    public const string GrantApplied = "approval.standing_grant";

    // Transformations
    public const string NoteMoved = "note.moved";
    public const string ProjectDeleted = "project.deleted";

    // Self-change
    public const string ChangeSetApplied = "changeset.applied";
    public const string ChangeSetReverted = "changeset.reverted";

    // External work
    public const string ExternalPackaged = "external.packaged";
    public const string ExternalResponded = "external.responded";
    public const string ArtifactStored = "artifact.stored";

    // Attention
    public const string AttentionShown = "attention.shown";
    public const string AttentionSuppressed = "attention.suppressed";
    public const string AttentionDismissed = "attention.dismissed";
}
