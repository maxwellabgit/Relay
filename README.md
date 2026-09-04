# Relay

A local-first Windows desktop orchestrator for your ideas, notes, projects, and approved agent work. One persistent, transparent system owns the conversation history, project memory, activity ledger, and file structure. An AI may *propose*; deterministic software enforces permissions and executes; you approve anything that changes a project.

Two chords drive it, active only while the Relay window is in front (nothing is registered system-wide, so `Ctrl+X` still cuts everywhere else), with Wispr Flow (or plain typing) supplying the words:

- **Ctrl+Alt · Silent Note Mode** — Relay stores what you said verbatim, extracts typed notes (decisions, tasks, questions, ideas) with exact character spans back to the transcript, files confident ones into projects, and asks in **Review** about the rest. It never replies.
- **Ctrl+X · Command Mode** — Relay shows *Ready*, records the instruction, plans with read-only tools, shows its reasoning and sources, and puts every change in front of you as a proposal to approve, edit, or reject.

Set `"hotkeys": { "scope": "global", "noteKey": "F13", "commandKey": "F14" }` to register system-wide hotkeys instead (a global chord needs a non-modifier key).

The product and security contract is `orchestrator_foundation_v0_1.md`. The engineering specifications are in `docs/`:

| Doc | Content |
| --- | --- |
| [01 Interaction contract](docs/01-interaction-contract.md) | every input, every mode, collisions, cancellation, failure choices |
| [02 State machine](docs/02-state-machine.md) | states, triggers, transition table, timers, ordering invariants |
| [03 Security and permissions](docs/03-security-and-permissions.md) | threat model, trust boundaries, authority tiers, action protocol |
| [04 Data model](docs/04-data-model.md) | ledger format and full event catalog, drafts, notes, sessions, settings |
| [05 Storage layout](docs/05-project-folder-structure.md) | data root and project folder contract |
| [06 Wispr Flow boundary](docs/06-wispr-flow-boundary.md) | what Relay assumes about Flow, the fixed-chord relay, setup |
| [07 Agent execution model](docs/07-agent-execution-model.md) | orchestrator vs workers, proposal → policy → capability → executor, sandbox |
| [08 Build plan](docs/08-build-plan.md) | phases 1–7, what is built, what is owed |

## What is built

| Layer | What it does |
| --- | --- |
| Control surface | hotkeys, isolated capture surface, visible state, hash-chained append-only ledger, cancellation, crash recovery, `LOCKED` on tamper |
| Local records | registered workspace roots, projects (create / rename / archive / restore, never delete), typed notes with front matter and spans, versioned writes, backup export + verification |
| Orchestrator | `PLANNING → AWAITING_APPROVAL → EXECUTING`; a deterministic command grammar first, an optional model (one https endpoint, key in DPAPI) for what the grammar does not understand; read-only tools; every tool call and model round trip in the ledger |
| Policy and execution | proposals → tier table → hash-bound approval → single-use capability → typed executor with a write journal; interrupted executions surface in Review |
| Memory | note extraction with spans, routing confidence (auto / Review / unrouted), disputes and supersession without erasure, recall with citations that mark superseded conclusions |
| Workers | sandboxed `Relay.Worker` child process (job object: kill-on-close, one process, memory cap, no UI), a broker as its only file access, hashed inputs, staging-only output, apply as a second approval |
| UI | Status · Capture · Response (reasoning, answer, sources, proposals) · Review · Projects/Workspaces/Staging · Activity · Diagnostics · Settings |

## Requirements

- Windows 11 (x64).
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0). Visual Studio is not required; the app is an unpackaged WinUI 3 application with a self-contained Windows App SDK.
- Wispr Flow, if you want voice input. Everything also works by typing (**Type an instruction** / **Type a note** buttons, or the hotkeys with the capture box focused).
- Optionally, an OpenAI-compatible chat-completions endpoint and API key for the model orchestrator. Without it Relay runs the rules grammar only and never opens a network connection.

## Build, test, run

```powershell
dotnet build Relay.slnx
dotnet test tests/Relay.Tests/Relay.Tests.csproj
dotnet run --project src/Relay.Desktop/Relay.Desktop.csproj
```

The executable is `src\Relay.Desktop\bin\x64\Debug\net10.0-windows10.0.26100.0\Relay.exe`, with `Relay.Worker.dll` beside it. One instance runs per data root; a second launch brings the first forward and exits. Override the data root with `RELAY_DATA_ROOT`.

## Using it

1. **Register a workspace** — *Projects → Add workspace folder…*. Relay writes only inside registered folders and its own data root.
2. **Create a project** — say `create project Atlas` (Ctrl+X) or use *New project…*. The proposal appears in **Response**; approve it. The folder gets `project.toml`, `.orchestrator\`, `notes\`, `decisions\`, `tasks\`, `artifacts\`.
3. **Take notes** — Ctrl+Alt, speak, Ctrl+Alt. *"We decided the Atlas beta ships on October 14. Need to email the Atlas pilot customers."* becomes a decision and a task filed under Atlas, each pointing at the exact characters of the capture. A sentence that names no project is routed by vocabulary overlap: filed when confident, otherwise it waits in **Review** with the candidate projects, or stays in staging. Conflicting decisions also wait in Review.
4. **Ask** — `what did I say about the beta?` returns the passages with their ledger spans; superseded decisions are marked as such and ranked below current ones.
5. **Delegate** — `summarize atlas` proposes a worker run (approve), which produces `staging\agents\{run}\out\summary.md` inside the sandbox. `apply the summary to atlas` is a second proposal that versions and writes `artifacts\summary.md`.
6. **Housekeeping** — `archive project Atlas`, `rename project Atlas to Atlas Beta`, `export a backup`. Deletion does not exist; `delete project X` is treated as archive and a request to purge is denied on record.

Grammar the rules understand: create / archive / restore / rename / list projects · `remember that …` · `file the last note under <project>` · `what's in <project>` · `what did I say about …` / `find …` / `recall …` · `summarize <project>` · `apply the summary to <project>` · `export a backup`. With **rules + model** enabled, anything else goes to the model, which can only call the same read-only tools and must cite ids the tools returned.

### Model gateway

*Settings → Orchestrator: Rules + model → Model gateway on → endpoint, model, API key → Save.* The key is encrypted with DPAPI for your Windows account under `config\secrets\` and never appears in settings or the ledger; the ledger records `model.requested`/`model.responded` with sizes and timing only. The gateway sends exactly one request shape (system prompt, the instruction, tool results) to exactly one https endpoint, with no redirects, proxy, retries, or telemetry.

### Wispr Flow

Flow types into whatever control has focus; Relay's capture box is focused and brought forward when a capture starts, and only text that lands there counts. Start and stop Flow with Flow's own shortcut, or let Relay send one fixed chord: set `"flowRelay": { "enabled": true, "handsFreeChord": "Ctrl+Win+F24" }` in `config\settings.json` and bind that chord to Flow's hands-free mode. Every emission or refusal is logged. Relay never reads the clipboard.

### Settings

`%LOCALAPPDATA%\Relay\config\settings.json`. Orchestrator, model, worker and threshold settings are edited in the **Settings** dialog and take effect on the next turn; hotkeys, relay and capture timing need a restart.

```json
{
  "schemaVersion": 1,
  "hotkeys": { "scope": "window", "noteKey": "Ctrl+Alt", "commandKey": "Ctrl+X" },
  "flowRelay": { "enabled": false, "handsFreeChord": "Ctrl+Win+F24", "startDelayMs": 200 },
  "capture": { "transcriptTimeoutMs": 10000, "stabilizationMs": 1500, "stabilizationWithoutRelayMs": 600, "completedReceiptMs": 4000, "draftPersistDebounceMs": 200 },
  "orchestrator": { "mode": "rules", "autoRouteThreshold": 0.75, "reviewThreshold": 0.35, "planningTimeoutMs": 60000, "maxToolCalls": 8 },
  "model": { "enabled": false, "endpoint": "https://api.openai.com/v1/chat/completions", "model": "gpt-4o-mini", "secretName": "model-gateway", "timeoutMs": 30000, "maxOutputTokens": 1500 },
  "workers": { "enabled": true, "wallClockSeconds": 120, "memoryMb": 512, "maxToolCalls": 400, "maxReadBytes": 8388608, "maxWriteBytes": 2097152 },
  "diagnostics": { "flowProcessNames": ["Wispr Flow", "WisprFlow", "Flow"] }
}
```

## What is on disk

```
%LOCALAPPDATA%\Relay\
  ledger\relay-ledger.jsonl       everything that happened — hash-chained, append-only
  registry\projects.json          project identities, slugs, aliases, roots, per-project policy
  config\settings.json            settings (hash recorded at every start)
  config\workspaces.json          registered roots
  config\secrets\*.bin            DPAPI-protected secrets (model API key)
  staging\drafts\current.json     the capture in progress (crash safety)
  staging\notes\*.json            extracted draft notes with spans; unrouted ones stay here
  staging\review\                 pending routing and dispute decisions
  staging\proposals\ turns\ executions\   proposal records, current turn, write journals (crash recovery)
  staging\agents\{runId}\         worker runs: spec, hashed input copies, out\, status
  archive\                        archived projects with manifests
  backups\                        verified zip exports
  sessions\*.json  incidents\*.json

<workspace>\<project-slug>\
  project.toml  notes\  decisions\  tasks\  artifacts\
  .orchestrator\versions\         every previous version of every file Relay rewrote
```

Nothing under the data root or a project is deleted by Relay. Tampering with the ledger is detected at the next start and puts Relay into `LOCKED` until you inspect and unlock; the break stays visible in the chain forever.

## Testing environment

`dotnet test` runs 460+ deterministic tests in about a second, plus one that launches the real worker process. Scenario tests script the whole system through user prompts against the real coordinator:

```csharp
using var s = Scenario.New(tmp).WithWorkspace()
    .Command("create project Atlas").Approve()
    .Note("We decided the Atlas beta ships on October 14. Need to email the Atlas pilot customers before then.")
    .ExpectEvent(EventTypes.NoteRouted, atLeast: 2)
    .Command("what did I say about the beta?")
    .ExpectOutcome("answered").ExpectAnswerContains("October 14");
```

Failures print the transcript: every state change, receipt, plan step, proposal decision and ledger line. `CannedOrchestrator` scripts plans; `ScriptedModelClient` scripts model replies (the JSON tool loop, fabricated-citation rejection, prohibited actions, malformed output, tool budget); `InProcessWorkerHost` runs the worker protocol on the test thread with hostile and hanging bodies; the real `Relay.Worker.dll` runs under a job object. A live model test runs only when `RELAY_LIVE_MODEL_KEY` is set.

## Repository layout

```
src/Relay.Core       pure .NET: state machine, coordinator, ledger, projects, notes, policy, executor, orchestrators, memory, agents (no UI / network / P-Invoke)
src/Relay.Gateway    the one HTTP client: OpenAI-compatible chat completions on a single https endpoint
src/Relay.Windows    Win32: hotkeys, foreground/focus, fixed-chord SendInput, ACL, single instance, DPAPI secrets, job-object worker host
src/Relay.Worker     dependency-free sandboxed worker (summarize), JSON lines over stdio
src/Relay.Desktop    WinUI 3 window, composition root, global exception handlers
tests/Relay.Tests    xUnit: scenario DSL, fakes, fault injection, in-process and real-process workers
docs/                specifications 01–08
```

## Status

Phases 1–6 of the build plan are implemented and covered by tests; the desktop UI exposes all of it. Still owed before a release: a period against real Wispr Flow, a live model run, a scripted UI Automation pass over the new regions, an outbound-connection block for workers, and signed MSIX packaging. See `docs/08-build-plan.md`.
