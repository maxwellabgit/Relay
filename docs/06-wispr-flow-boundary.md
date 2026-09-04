# 06 · Wispr Flow integration boundary

Wispr Flow turns microphone audio into text and types it into whatever control has keyboard focus. That is the entire integration. Flow owns nothing: not notes, not history, not commands, not the decision of what counts as a capture.

## 1. What Relay relies on

| Assumption about Flow | How Relay depends on it | If it changes |
| --- | --- | --- |
| Flow inserts text by simulating keyboard input into the focused control | Relay's capture `TextBox` is focused and foreground during capture; `TextChanged` is the only transcript signal | Relay shows *SURFACE NOT FOCUSED* and records `capture.focus_lost`; text goes nowhere Relay watches |
| Flow's hands-free mode is toggled by a user-configurable global shortcut | The optional relay adapter emits that one chord to start and stop Flow | Relay continues to work with the relay disabled; the user toggles Flow manually |
| Flow inserts the transcript shortly after the user stops speaking, possibly in bursts | `AWAITING_TRANSCRIPT` waits for a quiet period, not for a signal from Flow | Timeout and *Retry wait* cover slow insertion |
| Flow may fall back to the clipboard when insertion fails | Relay never reads the clipboard; the timeout notice tells the user to recover the text from Flow | No silent import ever happens |
| Flow's Context Awareness reads visible text | The capture window shows no project data, history, or notes during capture; users are told to disable Context Awareness for Relay | Reduces exposure regardless |

Relay does not use any Flow API, file, log, or window. It does not know or care which speech model Flow uses.

## 2. The adapter (`IFlowRelay`, `Relay.Windows.FixedChordRelay`)

```
IFlowRelay
  bool     Enabled
  KeyChord Chord                       // the one configured chord, e.g. Ctrl+Win+F24
  RelayResult SendHandsFreeToggle(RelayPurpose start|stop)
```

There is no method that takes a key. The implementation builds the `INPUT[]` array for exactly `Chord` (modifiers down, key down, key up, modifiers up) and calls `SendInput` once. The coordinator calls it only:

1. **Start:** `flowRelay.startDelayMs` after the surface is prepared, and only if `IsCaptureSurfaceForeground()` is true **and** the surface is still empty. Otherwise `flow.relay_skipped{reason}`.
2. **Stop:** on the second hotkey press or on Cancel, and only if the surface is still foreground. Otherwise `flow.relay_skipped`.

Every emission is `flow.relay_sent{purpose, chord, ok, error}`. No other synthetic input exists in the codebase, and the ledger never records synthetic key events other than this chord.

Default: **disabled**. The user must reserve a private Flow binding first (contract §17.3); until then Relay is hotkey-agnostic about Flow and the user starts and stops Flow with Flow's own shortcut.

## 3. Sequence (relay enabled)

```
user      Relay                                   Flow
 │ F13 ──▶ RegisterHotKey → NoteKey
 │         state IDLE→NOTE_CAPTURE, capture.started
 │         write current.json, focus surface, bring window forward
 │         wait startDelayMs, verify foreground && empty
 │         SendInput(Ctrl+Win+F24) ─────────────────▶ hands-free ON
 │ speaks…                                            transcribes
 │         TextChanged ◀──────────────────────────── keystrokes into the focused TextBox
 │         draft rewritten every ≤200 ms
 │ F13 ──▶ NoteKey: NOTE_CAPTURE→AWAITING_TRANSCRIPT, capture.stop_requested
 │         SendInput(Ctrl+Win+F24) ─────────────────▶ hands-free OFF, flushes remaining text
 │         TextChanged… quiet for stabilizationMs (1500)
 │         capture.transcript_stable → ORGANIZING → COMPLETED
```

Relay disabled: identical, minus the two `SendInput` lines; the user presses Flow's own shortcut, and the quiet period is `stabilizationWithoutRelayMs` (600 ms) because the user typically stops Flow before pressing the Relay key.

## 4. Failure states specific to Flow

| Symptom | Relay state | Ledger | User sees |
| --- | --- | --- | --- |
| Flow not running | capture proceeds, nothing arrives | `capture.transcript_timeout` after 10 s | notice; Diagnostics shows *Flow process: not detected* |
| Flow typed into another window | focus was lost first | `capture.focus_lost` | banner **Refocus**; the other app received the text, Relay did not |
| Flow slow | text arrives after stop | stabilization keeps restarting | *Text is arriving (N chars). Submitting once it stops changing…* |
| Flow clipboard fallback | nothing arrives | timeout | notice says to recover from Flow; Relay does not read the clipboard |
| Relay chord not bound in Flow | relay sent but Flow ignores it | `flow.relay_sent{ok:true}` then timeout | Diagnostics shows chord and Flow process; user fixes the Flow binding |
| Relay window not foreground when chord due | — | `flow.relay_skipped{reason:"capture surface is not foreground"}` | notice; user starts Flow manually |

## 5. User setup checklist

1. Install Wispr Flow; confirm it dictates into Notepad.
2. Choose Relay chords (`Ctrl+Alt` / `Ctrl+X` default, active only while the Relay window is in front). For system-wide hotkeys set `hotkeys.scope = "global"` with keyed chords such as `F13` / `F14` or `Ctrl+Alt+N` / `Ctrl+Alt+M` in `config\settings.json` and restart Relay.
3. Optional relay: in Flow, bind **Hands-free mode** to a chord nothing else uses (default `Ctrl+Win+F24`), then set `flowRelay.enabled = true` in settings. Start a capture and check **Activity** for `flow.relay_sent … ok`.
4. In Flow, disable Context Awareness for Relay (or globally).
5. Verify in **Diagnostics**: hotkeys registered, Flow process detected, capture surface focused during capture.

## 6. What would replace this adapter

If Flow ever offers a local transcript API, a second `IFlowRelay`/transcript source can be added behind the same coordinator with a new `capture.committed.source` value (e.g. `flow-api`). The capture-surface path stays as the fallback, and the ledger records which source produced each capture.
