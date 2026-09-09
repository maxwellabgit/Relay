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
            EventTypes.HotkeyRegistered => $"Registered {r.DataString("name")} as {r.DataString("chord")}" + (r.DataString("scope") == "window" ? " (this window only)" : ""),
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
            EventTypes.ApprovalRefused => $"Could not approve {r.DataString("action")} ({Short(r.DataString("proposalId"))}): {r.DataString("reason")}",
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
            EventTypes.AgentRunLaunched => $"Launched worker {Short(r.DataString("runId"))} ({r.DataString("task")} {r.DataString("projectSlug")}): {r.DataInt64("inputs")} input copies, no network, {r.DataString("host")}",
            EventTypes.AgentRunLog => $"Worker: {r.DataString("text")}",
            EventTypes.AgentRunToolCalled => $"Worker {Short(r.DataString("runId"))} used {r.DataString("tool")} {r.DataString("path")}" + (r.DataInt64("bytes") is { } b ? $" ({b} bytes)" : "") + (r.DataInt64("items") is { } n ? $" ({n} items)" : ""),
            EventTypes.AgentRunToolDenied => $"Worker {Short(r.DataString("runId"))} DENIED {r.DataString("tool")} {r.DataString("path")}: {r.DataString("reason")}",
            EventTypes.AgentRunCompleted => r.DataBool("ok") == true
                ? $"Worker {Short(r.DataString("runId"))} finished after {r.DataInt64("toolCalls")} tool call(s): {r.DataString("summary")}"
                : $"Worker {Short(r.DataString("runId"))} failed: {r.DataString("error")}",
            EventTypes.AgentRunTerminated => $"Worker {Short(r.DataString("runId"))} terminated: {r.DataString("reason")}",
            EventTypes.PatchApplied => $"Applied worker output {r.DataString("output")} to {r.DataString("projectSlug")}/{Path.GetFileName(r.DataString("destination") ?? "")}" + (r.DataString("previousVersionPath") is null ? "" : " (previous version kept)"),
            EventTypes.SettingsChanged => $"Settings changed (orchestrator {r.DataString("orchestratorMode")}, model {(r.DataBool("modelEnabled") == true ? "on" : "off")})",
            EventTypes.SecretChanged => $"Secret '{r.DataString("name")}' {r.DataString("action")}",
            EventTypes.AppFailed => $"Failure in {r.DataString("where")}: {r.DataString("exceptionType")}: {r.DataString("message")}",
            EventTypes.LockEngaged => $"LOCKED: {r.DataString("reason")}",
            EventTypes.LockReleased => "Unlocked by user",

            // Listening (ids, sizes and timings only)
            EventTypes.StreamStarted => $"Listening started ({(r.DataInt64("bufferSeconds") is > 0 and var b ? $"{b}s buffer" : "whole conversation held")}, judge {r.DataString("judge")})",
            EventTypes.StreamSegment => $"Heard a segment ({r.DataInt64("chars")} chars, {r.DataInt64("held")} held)",
            EventTypes.StreamStopped => $"Listening {r.DataString("reason")} after {Seconds(r)}: {r.DataInt64("segments")} segment(s), {r.DataInt64("passes")} check(s), {r.DataInt64("findings")} finding(s), {r.DataInt64("excerpts")} excerpt(s) kept",
            EventTypes.StreamInterruptedFound => $"A buffer window from a previous session was found and discarded ({r.DataInt64("segments")} segment(s), {r.DataInt64("chars")} chars)",
            EventTypes.ObserveChecked => $"{r.DataString("judge")} checked {Count(r, "segments")} new segment(s): nothing significant" + Tokens(r),
            EventTypes.ObserveFound => $"{r.DataString("judge")} found {Count(r, "findings")} significant thing(s) in {Count(r, "segments")} new segment(s)" + Tokens(r),
            EventTypes.ObserveFailed => $"Judge {r.DataString("judge")} failed: {r.DataString("error")}",
            EventTypes.ExcerptStored => $"Kept excerpt {Short(r.DataString("excerptId"))} ({FormatDouble(r, "seconds")}s, {r.DataInt64("chars")} chars{(r.DataBool("shrunkByGuard") == true ? ", trimmed by the retention guard" : "")}) for a {r.DataString("kind") ?? "finding"} finding: {r.DataString("why") ?? r.DataString("reason")}",
            EventTypes.AskRecorded => $"You asked ({r.DataInt64("chars")} chars)" + (r.DataBool("whileListening") == true ? " while listening" : ""),

            // Tasks
            EventTypes.TaskCreated => r.DataString("lane") == "user_operation" ? $"You requested: {r.DataString("title")}"
                // Overheard tasks: the title would quote the room, so the ledger holds a fingerprint; the line names the cue instead.
                : r.DataString("origin") == "observed" ? $"Overheard → {r.DataString("kind")} task {Short(r.DataString("taskId"))}: {r.DataString("why") ?? "finding"} ({FormatDouble(r, "confidence")})"
                : r.DataString("origin") == "dialogue" ? $"Follow-up → {r.DataString("kind")} task {Short(r.DataString("taskId"))}" + (r.DataBool("overheard") == true ? "" : $": {r.DataString("title")}")
                : $"{Capitalize(r.DataString("kind"))} task {Short(r.DataString("taskId"))} started with {r.DataString("planner")}",
            EventTypes.TaskPlanned => r.DataBool("understood") == true
                ? $"Plan by {r.DataString("producer")}: {r.DataString("summary")} ({r.DataInt64("proposals")} proposal(s), {r.DataInt64("toolCalls")} tool call(s)" + Tokens(r) + ")"
                : $"{r.DataString("producer")} did not understand the request",
            EventTypes.TaskPresented => r.DataString("surface") == "response"
                ? $"Shown in Response as {r.DataString("level")}: {r.DataString("title")}"
                : $"Shown as {r.DataString("level")}: {r.DataString("title")}" + (r.DataString("reason") is { } why ? $" ({why})" : ""),
            EventTypes.TaskMerged => $"Merged with an earlier card ({r.DataString("key")})",
            EventTypes.TaskCompleted => $"Task {Short(r.DataString("taskId"))} finished: {r.DataString("outcome")} ({r.DataInt64("executed")} executed, {r.DataInt64("denied")} denied, {r.DataInt64("rejected")} rejected" + Tokens(r) + $", {r.DataInt64("wallMs")} ms)",
            EventTypes.TaskFailed => $"Task {Short(r.DataString("taskId"))} failed ({r.DataString("failure")}): {r.DataString("error")}",
            EventTypes.TaskCancelled => $"Task {Short(r.DataString("taskId"))} cancelled while {r.DataString("stage")}",
            EventTypes.TaskUserResponse => $"You responded to task {Short(r.DataString("taskId"))}",
            EventTypes.TaskInterruptedFound => $"Found a {r.DataString("origin")} task that was {r.DataString("stage")} when the previous session ended",
            EventTypes.GrantApplied => r.DataString("noteId") is { } grantedNote
                ? $"Standing grant {Short(r.DataString("grantId"))} filed note {Short(grantedNote)} without asking"
                : r.DataString("proposalId") is null
                    ? $"Standing grant {Short(r.DataString("grantId"))} created for {r.DataString("action")}"
                    : $"Standing grant {Short(r.DataString("grantId"))} approved {r.DataString("action")} ({Short(r.DataString("proposalId"))})",

            // Transformations and self-change
            EventTypes.NoteMoved => $"Moved note {Short(r.DataString("noteId"))} to {Path.GetFileName(Path.GetDirectoryName(r.DataString("toPath") ?? "") ?? "")}",
            EventTypes.ProjectDeleted => $"Deleted project {r.DataString("slug")} ({r.DataInt64("files")} files; safety copy at {r.DataString("safetyBackup")})",
            EventTypes.ChangeSetApplied => $"Applied change set {Short(r.DataString("changeSetId"))} to {Path.GetFileName(r.DataString("path") ?? "")}" + (r.DataString("key") is { } key ? $" ({key})" : ""),
            EventTypes.ChangeSetReverted => $"Reverted change set {Short(r.DataString("changeSetId"))}: {r.DataString("reason")}",

            // External work
            EventTypes.ExternalPackaged => $"Packaged {r.DataInt64("sources")} source(s), {r.DataInt64("chars")} chars for {r.DataString("profile")} ({r.DataString("model")})",
            EventTypes.ExternalResponded => r.DataBool("ok") == true ? $"{r.DataString("profile")} answered ({r.DataInt64("chars")} chars" + Tokens(r) + $", {r.DataInt64("elapsedMs")} ms)" : $"{r.DataString("profile")} failed: {r.DataString("error")}",
            EventTypes.ArtifactStored => $"Stored artifact {Short(r.DataString("artifactId"))} ({r.DataInt64("chars")} chars)",

            // Attention
            EventTypes.AttentionShown => $"Shown [{r.DataString("level")}]: {r.DataString("title")}",
            EventTypes.AttentionSuppressed => $"Not shown ({r.DataString("reason")}) for task {Short(r.DataString("taskId"))}",
            EventTypes.AttentionDismissed => $"Dismissed card {Short(r.DataString("itemId"))}",
            _ => r.Type,
        };
        return new ActivityEntry(r.Seq, r.Timestamp, r.Type, text);
    }

    private static string Short(string? id) => id is null ? "?" : id.Length > 10 ? id[^8..] : id;

    private static string Capitalize(string? s) => string.IsNullOrEmpty(s) ? "Direct" : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Seconds(LedgerRecord r)
    {
        var s = FormatDouble(r, "seconds");
        return s == "n/a" ? "?" : s + "s";
    }

    private static string Tokens(LedgerRecord r)
    {
        var p = r.DataInt64("promptTokens") ?? 0;
        var c = r.DataInt64("completionTokens") ?? 0;
        return p + c == 0 ? "" : $", {p}+{c} tokens";
    }

    private static long Count(LedgerRecord r, string property)
    {
        try
        {
            return r.Data.TryGetProperty(property, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array ? v.GetArrayLength() : r.DataInt64(property) ?? 0;
        }
        catch (InvalidOperationException) { return 0; }
    }

    private static string FormatDouble(LedgerRecord r, string property)
    {
        try
        {
            return r.Data.TryGetProperty(property, out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetDouble().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
        }
        catch (InvalidOperationException) { return "n/a"; }
    }

    private static string FormatConfidence(LedgerRecord r)
    {
        try
        {
            return r.Data.TryGetProperty("confidence", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetDouble().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
        }
        catch (InvalidOperationException) { return "n/a"; }
    }
}
