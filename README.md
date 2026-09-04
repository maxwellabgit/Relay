# Relay

A local-first Windows control surface for one persistent orchestrator. This repository contains **slice 1**: the secure foundation that every later capability builds on — two global hotkeys, isolated voice-transcript capture, a state panel that never contradicts reality, an append-only hash-chained activity ledger, cancellation, failure handling, and crash recovery.

There is deliberately **no language model, no project mutation, no external integration, and no worker agent** in this build. Note mode stores what you said. Command mode records the instruction and does nothing else.

The product and security contract is `orchestrator_foundation_v0_1.md`. The engineering specifications are in `docs/`:

| Doc | Content |
| --- | --- |
| [01 Interaction contract](docs/01-interaction-contract.md) | every input, every mode, collisions, cancellation, failure choices, acceptance mapping |
| [02 State machine](docs/02-state-machine.md) | states, triggers, complete transition table, timers, ordering invariants |
| [03 Security and permissions](docs/03-security-and-permissions.md) | threat model, trust boundaries, authority tiers, action protocol |
| [04 Data model](docs/04-data-model.md) | ledger format and event catalog, drafts, notes, sessions, settings, incidents |
| [05 Storage layout](docs/05-project-folder-structure.md) | data root (now) and project folder contract (later) |
| [06 Wispr Flow boundary](docs/06-wispr-flow-boundary.md) | what Relay assumes about Flow, the fixed-chord relay, failure states, setup |
| [07 Agent execution model](docs/07-agent-execution-model.md) | orchestrator vs workers, proposal → policy → capability → executor, sandbox |
| [08 Build plan](docs/08-build-plan.md) | phases 1–7 with exit criteria; status of this slice |

## Requirements

- Windows 11 (x64).
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) (`dotnet --version` → 10.0.x). Visual Studio is not required; the app is built as an unpackaged WinUI 3 application with a self-contained Windows App SDK, so no runtime installer is needed either.
- Wispr Flow, if you want voice input. Relay works without it: anything typed into the capture surface is a capture.

## Build, test, run

```powershell
git clone <this repo> Relay
cd Relay
dotnet build Relay.slnx
dotnet test tests/Relay.Tests/Relay.Tests.csproj
dotnet run --project src/Relay.Desktop/Relay.Desktop.csproj
```

The executable is `src\Relay.Desktop\bin\x64\Debug\net10.0-windows10.0.26100.0\Relay.exe`. Only one instance runs per data root; launching a second brings the first forward and exits.

## First run

1. Relay creates `%LOCALAPPDATA%\Relay`, restricts it to your account and SYSTEM, writes default settings, and registers `F13` (NOTE_KEY) and `F14` (COMMAND_KEY).
2. The window shows `IDLE`. The status chips confirm both hotkeys registered (green dots). If one is red, another application owns that key — see **Review**, then edit `config\settings.json` and restart.
3. Press `F13`, speak (or type), press `F13` again. Watch the state go `NOTE_CAPTURE → AWAITING_TRANSCRIPT → ORGANIZING → COMPLETED`, then read the receipt: *Saved 1 draft note · routing deferred*.
4. Press `F14` for command mode. A **Ready** heading appears; the instruction is recorded verbatim and shown in **Review**; no tool runs.
5. `Esc` (while the capture box is focused) or **Cancel** abandons a capture without storing text; the text is offered once in Review as *Recover draft* / *Forget*.

### Wispr Flow

Flow types into whatever control has focus; Relay's capture box is focused and brought forward when a capture starts, and only text that lands there counts. Start and stop Flow with Flow's own shortcut, or let Relay do it:

1. In Flow, bind **Hands-free mode** to a chord nothing else uses (default expected: `Ctrl+Win+F24`).
2. In `%LOCALAPPDATA%\Relay\config\settings.json` set `"flowRelay": { "enabled": true, "handsFreeChord": "Ctrl+Win+F24", "startDelayMs": 200 }` and restart Relay.
3. Disable Flow's Context Awareness for Relay.

Relay can emit only that one chord, only when its own capture box is the foreground target, and logs every emission (`flow.relay_sent`) or refusal (`flow.relay_skipped`). It never reads the clipboard.

### Settings

`%LOCALAPPDATA%\Relay\config\settings.json` (override the whole data root with the `RELAY_DATA_ROOT` environment variable):

```json
{
  "schemaVersion": 1,
  "hotkeys": { "noteKey": "F13", "commandKey": "F14" },
  "flowRelay": { "enabled": false, "handsFreeChord": "Ctrl+Win+F24", "startDelayMs": 200 },
  "capture": { "transcriptTimeoutMs": 10000, "stabilizationMs": 1500, "stabilizationWithoutRelayMs": 600,
               "completedReceiptMs": 4000, "draftPersistDebounceMs": 200 },
  "diagnostics": { "flowProcessNames": ["Wispr Flow", "WisprFlow", "Flow"] }
}
```

Chords: `[Ctrl+][Alt+][Shift+][Win+]Key`, e.g. `Ctrl+Alt+N`. Invalid values fall back to defaults and are shown in Review; they never stop Relay from starting.

## What is on disk

```
%LOCALAPPDATA%\Relay\
  ledger\relay-ledger.jsonl     every state change, capture, failure, and recovery — hash-chained, append-only
  staging\notes\*.json          verbatim draft notes with a source span into the ledger
  staging\drafts\current.json   the capture in progress (crash safety); gone once stored
  sessions\*.json               run records used to detect crashes
  incidents\*.json              failures, including ones the ledger could not record
  config\settings.json
```

Read the ledger in plain language:

```powershell
Get-Content "$env:LOCALAPPDATA\Relay\ledger\relay-ledger.jsonl" | ForEach-Object {
  $o = $_ | ConvertFrom-Json; "{0}  {1,-28} {2}" -f $o.ts.Substring(11,12), $o.type, ($o.data | ConvertTo-Json -Compress -Depth 5) }
```

Nothing under the data root is ever deleted by Relay except the redundant `current.json` after its content has been committed. Tampering with the ledger is detected at the next start and puts Relay into `LOCKED` until you inspect and unlock; the break stays visible in the chain forever.

## Verification performed on this slice

Automated: `dotnet test` — ledger encode/verify/repair/tamper, exhaustive transition table, note and command flows, timeout and stabilization, Esc cancel with recover/forget, retry after a storage fault without duplicating the commit, crash recovery with interrupted draft, torn-tail repair, lock on tamper and on unwritable ledger.

Manual, on Windows 11 against a scratch `RELAY_DATA_ROOT`, driving hotkeys and text with `SendKeys` and buttons through UI Automation:

- `F13` → typed text → `F13`: `capture.committed` (sha256, verbatim text), `note.draft_created`, `COMPLETED` receipt, auto return to `IDLE`.
- `F14`: **Ready** heading, `command.recorded{executed:false}`, instruction shown in Review.
- `Esc` mid-capture: `capture.cancelled{chars}` with no text in the ledger; Review offers Recover draft / Forget.
- `taskkill /F` mid-capture, relaunch: `session.crash_detected`, `capture.interrupted_found{chars:40}`, Review → **Commit as captured** → `capture.committed` for the original capture id; chain intact across the crash.
- Second instance: exits with code 0, first instance comes forward.
- Window close: `session.ended{reason:"user_exit"}`; next start reports no crash.

## Repository layout

```
src/Relay.Core       pure .NET: state machine, coordinator, ledger, storage, settings, recovery (no UI / network / P-Invoke)
src/Relay.Windows    Win32 wrappers: RegisterHotKey listener, foreground/focus, single fixed-chord SendInput, ACL, single instance
src/Relay.Desktop    WinUI 3 window, composition root, global exception handlers
tests/Relay.Tests    xUnit, deterministic clock and scheduler, fault-injecting ledger
docs/                specifications 01–08
```

## Status

Slice 1 is functionally complete and verified. Before phase 2 begins: run against real Flow for a period, confirm hotkey and relay decisions (contract §17), and switch the build to signed MSIX. See `docs/08-build-plan.md`.
