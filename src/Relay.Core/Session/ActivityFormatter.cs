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
            EventTypes.CommandRecorded => $"Recorded instruction ({r.DataInt64("chars")} chars); orchestrator {r.DataString("orchestrator") ?? "off"}",

            // Orchestrator turns
            EventTypes.TurnStarted => r.DataString("kind") == "user_operation" ? $"You requested: {r.DataString("title")}" : $"Turn {Short(r.DataString("turnId"))} started with {r.DataString("orchestrator")}",
            EventTypes.TurnProgress => r.DataString("text") ?? "…",
            EventTypes.ToolCalled => $"Tool {r.DataString("tool")} called",
            EventTypes.ToolReturned => $"Tool {r.DataString("tool")} → {r.DataString("summary")}",
            EventTypes.ModelRequested => $"Asked model {r.DataString("model")} at {r.DataString("host")} ({r.DataInt64("promptChars")} prompt chars, {r.DataInt64("sources")} sources)",
            EventTypes.ModelResponded => r.DataBool("ok") == true ? $"Model answered ({r.DataInt64("chars")} chars in {r.DataInt64("elapsedMs")} ms)" : $"Model call failed: {r.DataString("error")}",
            EventTypes.PlanProposed => r.DataBool("understood") == true ? $"Plan by {r.DataString("producer")}: {r.DataString("summary")} ({r.DataInt64("proposals")} proposal(s))" : $"{r.DataString("producer")} did not understand the instruction",
            EventTypes.TurnCompleted => $"Turn finished: {r.DataString("outcome")} ({r.DataInt64("executed")} executed, {r.DataInt64("denied")} denied, {r.DataInt64("rejected")} rejected)",
            EventTypes.TurnCancelled => $"Instruction cancelled while {r.DataString("stage")}",
            EventTypes.TurnFailed => $"Instruction failed: {r.DataString("error")}",
            EventTypes.TurnInterruptedFound => $"Found an instruction that was {r.DataString("stage")} when the previous session ended",

            // Proposals and execution
            EventTypes.ProposalReceived => $"Proposal {Short(r.DataString("proposalId"))}: {r.DataString("action")} (by {r.DataString("proposedBy")})",
            EventTypes.ProposalDecided => $"Policy: {r.DataString("action")} → {r.DataString("outcome")} ({r.DataString("tier")})",
            EventTypes.ProposalEdited => $"Edited proposal {Short(r.DataString("fromProposalId"))} → {Short(r.DataString("toProposalId"))}",
            EventTypes.ApprovalGranted => $"Approved {r.DataString("action")} ({Short(r.DataString("proposalId"))})" + (r.DataBool("implicitViaUi") == true ? " by direct request" : ""),
            EventTypes.ApprovalRejected => $"Rejected {r.DataString("action")} ({Short(r.DataString("proposalId"))}): {r.DataString("reason")}",
            EventTypes.ExecutionStarted => $"Executing {r.DataString("action")} ({Short(r.DataString("proposalId"))})",
            EventTypes.ExecutionCompleted => $"Done: {r.DataString("summary")}",
            EventTypes.ExecutionFailed => $"Execution of {r.DataString("action")} failed at {r.DataString("stage")}: {r.DataString("error")}",
            EventTypes.ExecutionStopRequested => "Stop requested; finishing the current operation only",
            EventTypes.ExecutionInterruptedFound => $"Operation {r.DataString("action")} ({Short(r.DataString("proposalId"))}) was interrupted in a previous session",

            // Projects, notes, backups
            EventTypes.WorkspaceRegistered => $"Registered workspace {r.DataString("path")}",
            EventTypes.WorkspaceRemoved => $"Removed workspace {r.DataString("path")}",
            EventTypes.ProjectCreated => $"Created project '{r.DataString("name")}' ({r.DataString("slug")}) at {r.DataString("path")}",
            EventTypes.ProjectRenamed => $"Renamed project {r.DataString("oldSlug")} → {r.DataString("newSlug")}",
            EventTypes.ProjectArchived => $"Archived project {r.DataString("slug")} → {r.DataString("newPath")} ({r.DataInt64("files")} files)",
            EventTypes.ProjectRestored => $"Restored project {r.DataString("slug")} to {r.DataString("path")}",
            EventTypes.NoteExtracted => $"Extracted {r.DataInt64("count")} note(s) from capture {Short(r.DataString("captureId"))}",
            EventTypes.NoteRouted => $"Filed note {Short(r.DataString("noteId"))} under {r.DataString("projectSlug")} (confidence {r.DataString("confidence") ?? FormatConfidence(r)}, by {r.DataString("by")})",
            EventTypes.NoteRoutingDeferred => $"Routing of note {Short(r.DataString("noteId"))} needs your decision: {r.DataString("reason")}",
            EventTypes.NoteWritten => $"Wrote note {Short(r.DataString("noteId"))} v{r.DataInt64("version")} ({Short(r.DataString("sha256"))})",
            EventTypes.NoteModified => $"Modified note {Short(r.DataString("noteId"))} → v{r.DataInt64("version")}; previous kept",
            EventTypes.NoteDisputed => $"Notes {Short(r.DataString("noteId"))} and {Short(r.DataString("otherNoteId"))} marked disputed: {r.DataString("reason")}",
            EventTypes.NoteSuperseded => $"Note {Short(r.DataString("oldNoteId"))} superseded by {Short(r.DataString("newNoteId"))}",
            EventTypes.BackupExported => $"Exported backup ({r.DataInt64("files")} files) to {r.DataString("path")}",
            EventTypes.BackupVerified => r.DataBool("ok") == true ? "Backup verified against its manifest" : "Backup verification FAILED",

            // Workers
            EventTypes.AgentRunLaunched => $"Launched worker {Short(r.DataString("runId"))} ({r.DataString("task")}) pid {r.DataInt64("pid")}",
            EventTypes.AgentRunToolCalled => $"Worker {Short(r.DataString("runId"))} used {r.DataString("tool")}: {r.DataString("path") ?? r.DataString("summary")}",
            EventTypes.AgentRunToolDenied => $"Worker {Short(r.DataString("runId"))} DENIED {r.DataString("tool")}: {r.DataString("reason")}",
            EventTypes.AgentRunCompleted => $"Worker {Short(r.DataString("runId"))} finished: {r.DataInt64("outputs")} output file(s)",
            EventTypes.AgentRunTerminated => $"Worker {Short(r.DataString("runId"))} terminated: {r.DataString("reason")}",
            EventTypes.PatchApplied => $"Applied worker output to {r.DataString("path")}",
            EventTypes.SettingsChanged => $"Settings changed (orchestrator {r.DataString("orchestratorMode")}, model {(r.DataBool("modelEnabled") == true ? "on" : "off")})",
            EventTypes.SecretStored => $"Stored secret '{r.DataString("name")}' (DPAPI, this user only)",
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

    private static string FormatConfidence(LedgerRecord r)
    {
        try
        {
            return r.Data.TryGetProperty("confidence", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetDouble().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
        }
        catch (InvalidOperationException) { return "n/a"; }
    }
}
