# 01 · Interaction contract

Status: implemented in slice 1 unless marked *later*.
Parent: `orchestrator_foundation_v0_1.md` §3–§4, §18.

Relay has exactly two chord inputs (`NOTE_KEY`, `COMMAND_KEY`), one local key (`Esc`, only while Relay's capture surface is focused), and a small set of visible buttons. Everything the user can do is listed here; nothing else exists.

## 1. Inputs

| Input | Scope | Default | Configurable |
| --- | --- | --- | --- |
| `NOTE_KEY` | `hotkeys.scope`: `window` (default; matched by the Relay window only while it is active, nothing registered system-wide) or `global` (`RegisterHotKey`) | `Ctrl+Alt` | `config\settings.json → hotkeys.noteKey` |
| `COMMAND_KEY` | same scope as `NOTE_KEY` | `Ctrl+X` | `hotkeys.commandKey` |
| `Esc` | Only when the capture surface has keyboard focus | fixed | no |
| Buttons | Only in the Relay window | — | — |

A modifier-only chord (`Ctrl+Alt`) is valid in `window` scope, where it fires when its last modifier goes down with exactly the others held; `global` scope needs a non-modifier key (`Ctrl+Alt+N`). `hotkey.registered` records the scope.

Rules:

- A hotkey is a toggle. The first press always *starts* the named mode; the second press of the **same** key always *attempts to stop* that mode. A key never does anything else.
- If a hotkey cannot be registered (another application owns it), Relay starts anyway, shows the failure in **Review** and in the status chips, and records `hotkey.registration_failed`. It never silently picks a different key.
- Relay never installs a keyboard hook and never reads keys other than its two registered chords.
- Both hotkeys are rejected while `STARTING` and `LOCKED`, and while another capture is running (see §4).

## 2. Note mode (`NOTE_KEY`)

### 2.1 Start (from `IDLE` or `COMPLETED`)

1. State becomes `NOTE_CAPTURE`; status shows the state label, the word *silent note*, elapsed time, character count, and whether the capture surface is focused.
2. A `capture.started` record is appended (capture ULID, mode, previous foreground process *name*, whether the Flow relay is enabled).
3. The capture surface is cleared, Relay's window is brought to the foreground, the surface receives keyboard focus. A crash-safe draft (`staging\drafts\current.json`) is written immediately with empty text.
4. If the Flow relay is enabled, after `flowRelay.startDelayMs` Relay emits the single configured Flow hands-free chord, and only if its own surface is foreground and still empty; otherwise it records `flow.relay_skipped` with the reason and the user starts Flow manually.
5. Only text that lands in the capture surface is accepted. Every change updates the draft (debounced `capture.draftPersistDebounceMs`, default 200 ms).
6. No project data, notes, or history is shown around the surface while capturing.

### 2.2 Stop (`NOTE_KEY` again while `NOTE_CAPTURE`)

1. State becomes `AWAITING_TRANSCRIPT`; `capture.stop_requested` records the character count at that instant.
2. If the relay is enabled, the same fixed chord is emitted once to stop Flow.
3. Relay waits for the transcript to *stabilize*: no text change for `stabilizationMs` (1500 ms with relay) or `stabilizationWithoutRelayMs` (600 ms without). Text may keep arriving during this window; each change restarts the quiet timer.
4. If no text has arrived within `transcriptTimeoutMs` (10 s) the state stays `AWAITING_TRANSCRIPT`, `capture.transcript_timeout` is recorded, and the user is offered **Retry wait**, **Submit now** (only if text exists), or **Cancel**. Relay never reads the clipboard; the notice tells the user to recover the dictation from Flow if Flow shows it.
5. On `capture.transcript_stable` (or **Submit now**) the state becomes `ORGANIZING`.

### 2.3 Organizing (no model)

1. The verbatim text is appended to the ledger as `capture.committed` with `sha256`, timing, `mode` and `source: "capture-surface"`. This record is the authoritative source for the capture.
2. A draft note is written to `staging\notes\{noteId}.json` containing the text plus a `SourceSpan` pointing at the `capture.committed` event id and character range. `note.draft_created` is appended.
3. The staging draft `current.json` is removed **only after** step 2 succeeds.
4. State becomes `COMPLETED` with the receipt `Saved 1 draft note · routing deferred (orchestrator not enabled in this build)`. After `completedReceiptMs` (4 s) or **Return to Idle**, state returns to `IDLE`.

Relay produces no reply, summary, question, or sound in Note mode. *Later:* the receipt may read `Saved 2 notes · 1 routing decision needs review` once extraction exists.

## 3. Command mode (`COMMAND_KEY`)

Identical mechanics to Note mode with these differences:

- State is `COMMAND_CAPTURE`; the mode label is *instruction*; a visual **Ready** heading appears above the capture surface for the duration of the capture. There is no spoken greeting or earcon in this slice.
- Organizing appends `capture.committed` and then `command.recorded` with `executed: false`. **No tool runs, no plan is produced, no file changes.**
- The exact instruction is shown in **Review** as *Instruction recorded — orchestrator not enabled in this build* until the next capture starts.
- *Later:* `ORGANIZING` is followed by `PLANNING → AWAITING_APPROVAL → EXECUTING`, each visible with concrete activity events, and every proposed action goes through the policy engine (see `03-security-and-permissions.md`).

## 4. Mode collisions

Exactly one capture exists at a time.

| While in | Press | Result |
| --- | --- | --- |
| `NOTE_CAPTURE` | `COMMAND_KEY` | Rejected. Notice: **Finish or cancel the active capture first.** Recorded as `hotkey.rejected`. |
| `COMMAND_CAPTURE` | `NOTE_KEY` | Same. |
| `AWAITING_TRANSCRIPT` | either key | Rejected. Notice: **Waiting for the transcript. Use Submit now or Cancel.** |
| `ORGANIZING` | either key | Rejected: **Still organizing the previous capture.** |
| `FAILED` | either key | Rejected: **Inspect the failure first: Retry or Return to Idle.** |
| `LOCKED` | either key | Rejected: **Relay is locked. Inspect the incident, then unlock explicitly.** |

A rejection never changes the mode, never submits the existing text, and never clears the surface.

## 5. Cancellation

- **Cancel (Esc)** is visible during `NOTE_CAPTURE`, `COMMAND_CAPTURE`, and `AWAITING_TRANSCRIPT`. `Esc` works only while the capture surface has focus; it is not a global hotkey.
- Cancel never submits partial text. It appends `capture.cancelled` with the character *count* and the state at cancel — never the text.
- If the relay is enabled and Flow was started by Relay, the fixed stop chord is emitted so Flow does not keep listening.
- The cancelled text is held **in memory only** and offered in Review as *Cancelled note/command capture (N chars)* with **Recover draft** (stores it as a normal capture via `capture.draft_recovered → ORGANIZING`) or **Forget**. It is gone when Relay exits.
- `ORGANIZING` cannot be cancelled: it only stores what was already captured. The button is hidden and the trigger is rejected with an explanation.

## 6. Failure modes and their recovery choices

| Failure | Detection | State | User choices |
| --- | --- | --- | --- |
| No transcript | `transcriptTimeoutMs` elapsed with 0 chars | `AWAITING_TRANSCRIPT` + notice | Retry wait · Cancel |
| Transcript never quiets | timeout elapsed with text present | treated as stable → `ORGANIZING` | — |
| Focus lost during capture | window deactivated or surface lost focus | unchanged, `capture.focus_lost`, banner **Refocus** | Refocus · Cancel · continue |
| Flow relay could not send / surface not foreground | `SendInput` result / foreground check | unchanged, `flow.relay_sent{ok:false}` or `flow.relay_skipped` | Start Flow manually · Cancel |
| Storage failure while organizing | exception in commit or note write | `FAILED` (`capture.organize_failed`) | Retry (never duplicates a committed capture) · Return to Idle |
| Unhandled exception anywhere | global handlers | `FAILED` (`app.failed`) + incident file | Return to Idle · Open incident file |
| Ledger cannot be appended | write/flush throws | `LOCKED` (`lock.engaged`) | Inspect · Unlock |
| Ledger tampered / hash chain broken | startup verification | `LOCKED` (`ledger.integrity_failed`) | Inspect file · Unlock (acknowledged in `lock.released`) |
| Torn last line (crash mid-write) | startup verification | repaired: tail bytes moved to `ledger\quarantine\`, `ledger.repaired` | none needed |
| Process died mid-capture | session record never ended + `current.json` exists | `IDLE` with Review item *Interrupted capture* | Commit as captured · Discard to staging |
| Second instance launched | named mutex per data root | new instance exits, running one comes forward | — |

Every row above is visible in **Activity** and, where a decision is needed, in **Review**.

## 7. What the window always shows

Four permanent regions (contract §6), top to bottom:

1. **Status** — state label + colour dot (never colour alone), mode, elapsed time, character count, focus state, hotkey chips with registration status, Flow relay chip, ledger health and record count, session id, pid, version.
2. **Capture / response** — the isolated `TextBox`; *Ready* heading in command mode; contextual buttons (Cancel, Submit now, Retry wait, Return to Idle, Retry, Unlock); notice line; receipt line.
3. **Activity** — chronological plain-language lines, one per ledger record, with the raw event type on the right and the current chain tail hash.
4. **Review** — items awaiting a decision: interrupted capture, cancelled draft, incident, settings problem, hotkey problem, recorded instruction.

Plus a collapsed **Diagnostics** expander: data root, ledger path/health/tail, session, settings hash and problems, hotkey registration, relay configuration, whether a Flow process is detected, foreground process name and focus, timeouts, window handle and DPI scale.

## 8. Acceptance criteria (contract §18) → how each is met

| Criterion | Mechanism | Evidence |
| --- | --- | --- |
| One press starts, second press stops the same mode | `TransitionTable` only accepts `NoteKey` from `NOTE_CAPTURE` and `CommandKey` from `COMMAND_CAPTURE` | `TransitionTableTests`, `CaptureFlowTests` |
| Modes never overlap or convert | the other key is rejected in every capturing state | `CaptureFlowTests.OtherKeyDuringCaptureIsRejected…` |
| UI never contradicts capture state | UI renders only from `RelaySnapshot`; the snapshot is derived from the coordinator's single state field | `MainWindow.Render` |
| No transcript under the wrong mode | mode is fixed in the draft at `capture.started` and copied into `capture.committed` | `CaptureFlowTests` |
| Every transition and failure visible and logged | `state.changed` per accepted transition; `hotkey.rejected` per rejection; all failures are events | live ledger dump in README |
| Note mode produces no response | organizing writes a file and a receipt; no other output path exists | code |
| Command mode begins with Ready and preserves the exact instruction | `Ready` heading; `capture.committed.text` is the verbatim surface text with `sha256` | live run |
| Loss of network/focus/Flow/process cannot create an unlogged canonical change | this slice has no canonical writes; staging writes happen only after their ledger record | design |
| Restart returns to a safe state and exposes interrupted capture | `StartupRecovery` + Review item with Commit/Discard | `RecoveryAndFailureTests`, live crash test |
| Nothing can permanently delete a project | no delete code path exists; drafts are moved, never deleted, except the redundant staging copy after commit | code |
