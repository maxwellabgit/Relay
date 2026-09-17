# 02 · State machine

Source of truth: `src/Relay.Core/State/TransitionTable.cs` (pure function) and `src/Relay.Core/Session/SessionCoordinator.cs` (side effects). The table below is the complete contract; tests enumerate every `(state, trigger)` pair.

## 1. States

| State | Wire label | Primary? | In slice 1 |
| --- | --- | --- | --- |
| `Starting` | `STARTING` | yes | yes — ledger verification and recovery run before the UI accepts input |
| `Idle` | `IDLE` | yes | yes |
| `NoteCapture` | `NOTE_CAPTURE` | yes | yes |
| `CommandCapture` | `COMMAND_CAPTURE` | yes | yes |
| `AwaitingTranscript` | `AWAITING_TRANSCRIPT` | yes | yes |
| `Organizing` | `ORGANIZING` | yes | yes (local storage only) |
| `Planning` | `PLANNING` | yes | reserved; every trigger rejected with *Not available in this build* |
| `AwaitingApproval` | `AWAITING_APPROVAL` | yes | reserved |
| `Executing` | `EXECUTING` | yes | reserved |
| `Completed` | `COMPLETED` | yes | yes |
| `Failed` | `FAILED` | yes | yes |
| `Locked` | `LOCKED` | yes | yes |

Exactly one primary state is ever shown. Secondary facts (mode, elapsed time, character count, focus, timeout flag, stabilization pending) are carried on the snapshot, never encoded as extra states.

## 2. Triggers

| Trigger | Origin |
| --- | --- |
| `NoteKey`, `CommandKey` | global hotkeys |
| `Cancel` | Cancel button, `Esc` in the surface |
| `SubmitNow`, `RetryWait` | buttons in `AWAITING_TRANSCRIPT` |
| `TranscriptStable`, `TranscriptTimeout` | scheduler timers |
| `OrganizeSucceeded`, `OrganizeFailed` | result of `Organize()` |
| `Dismiss` | Return to Idle button, receipt timer |
| `Retry` | Retry button in `FAILED` (only when `CanRetry`) |
| `CommitInterrupted`, `RecoverDraft` | Review actions |
| `Fail` | any reported exception (`ReportFailure`) |
| `Lock` | ledger append failure |
| `Unlock` | Unlock button |
| `RecoveryCompleted`, `RecoveryLocked` | end of `Start()` |

## 3. Transition table

Global rules evaluated first:

- `Lock` from any state except `Locked` → `Locked`.
- `Fail` from any state except `Locked` or `Failed` → `Failed`. A lock is never downgraded to a failure.

| From | Trigger | To | Notes |
| --- | --- | --- | --- |
| `Starting` | `RecoveryCompleted` | `Idle` | |
| `Starting` | `RecoveryLocked` | `Locked` | ledger integrity failure |
| `Starting` | anything else | reject | *Relay is still starting.* |
| `Idle` / `Completed` | `NoteKey` | `NoteCapture` | side effect: `BeginCapture(Note)` |
| `Idle` / `Completed` | `CommandKey` | `CommandCapture` | side effect: `BeginCapture(Command)` |
| `Completed` | `Dismiss` | `Idle` | |
| `Idle` | `CommitInterrupted` | `Organizing` | interrupted draft becomes the active draft |
| `Idle` | `RecoverDraft` | `Organizing` | cancelled text becomes a new capture |
| `Idle` / `Completed` | anything else | reject | *Nothing to … while IDLE.* |
| `NoteCapture` | `NoteKey` | `AwaitingTranscript` | side effect: `RequestStop()` |
| `NoteCapture` | `CommandKey` | reject | *Finish or cancel the active capture first.* |
| `NoteCapture` | `Cancel` | `Idle` | |
| `CommandCapture` | `CommandKey` | `AwaitingTranscript` | `RequestStop()` |
| `CommandCapture` | `NoteKey` | reject | *Finish or cancel the active capture first.* |
| `CommandCapture` | `Cancel` | `Idle` | |
| `AwaitingTranscript` | `TranscriptStable` | `Organizing` | |
| `AwaitingTranscript` | `SubmitNow` | `Organizing` | requires text |
| `AwaitingTranscript` | `TranscriptTimeout` | `AwaitingTranscript` | self-transition; sets the timed-out flag and notice |
| `AwaitingTranscript` | `RetryWait` | `AwaitingTranscript` | restarts the timeout |
| `AwaitingTranscript` | `Cancel` | `Idle` | |
| `AwaitingTranscript` | `NoteKey` / `CommandKey` | reject | *Waiting for the transcript…* |
| `Organizing` | `OrganizeSucceeded` | `Completed` | |
| `Organizing` | `OrganizeFailed` | `Failed` | `CanRetry` becomes true |
| `Organizing` | `Cancel` | reject | organizing only stores what was already captured |
| `Organizing` | anything else | reject | *Still organizing the previous capture.* |
| `Failed` | `Dismiss` | `Idle` | |
| `Failed` | `Retry` | `Organizing` | only after `OrganizeFailed`; never re-appends an already committed capture |
| `Failed` | anything else | reject | *Inspect the failure first…* |
| `Locked` | `Unlock` | `Idle` | appends `lock.released{acknowledged, by:"user"}` first; stays locked if that append fails |
| `Locked` | anything else | reject | *Relay is locked…* |
| `Planning` / `AwaitingApproval` / `Executing` | anything | reject | *Not available in this build.* |

Every accepted transition that changes state appends `state.changed{from,to,trigger}`. Every rejected hotkey appends `hotkey.rejected{key,state,reason}` so the user's press is never silently swallowed.

## 4. Timers (all via `IScheduler`, deterministic in tests)

| Timer | Started | Cancelled by | Fires |
| --- | --- | --- | --- |
| relay start | `BeginCapture` when relay enabled | stop, cancel, failure | emits the Flow start chord if surface still foreground and empty |
| transcript timeout | `RequestStop`, `RetryWait` | stable, submit, cancel | `TranscriptTimeout` (or stable, if text exists) |
| stabilization | any text change in `AwaitingTranscript`, and on `RequestStop` if text already present | next text change, cancel, submit | `TranscriptStable` |
| draft persist | first text change after last flush | — | atomic rewrite of `current.json` |
| receipt | `Completed` | new capture, Dismiss | `Dismiss` |

Timer callbacks carry a generation number and are ignored if the await generation, state, or capture id changed in the meantime.

## 5. Side-effect ordering invariants

1. **Ledger before storage.** `capture.committed` is appended before the draft note is written; `note.draft_created` / `command.recorded` is appended after the file exists. The staging draft is removed only after both succeed.
2. **Draft before surface.** `current.json` is written before the capture surface is focused, so a crash at any moment during capture leaves a draft on disk.
3. **Transition before effect.** `Apply(trigger)` must accept before any side effect runs; a rejected trigger only produces a notice and (for hotkeys) a ledger record.
4. **Append failure locks.** If any `Append` throws, the coordinator moves to `Locked`, writes an incident file, and stops doing work. Nothing continues on an unwritable ledger.
5. **Retry is idempotent.** If `capture.committed` succeeded but the note write failed, retry skips the commit and only writes the note; `capture.organize_failed.committed` records which case occurred.
