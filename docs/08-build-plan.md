# 08 · Incremental build plan

Each phase ships only when its exit criteria hold and the previous phase's ledger, state machine, and tests are unchanged except by addition. Intelligence is added last to the parts that can do the least harm.

Status legend: ✅ built and covered by tests · 🟡 built, live verification still owed · ⬜ not started.

## Phase 1 — Control surface ✅

Scope: two hotkeys, isolated capture, visible state, append-only hash-chained ledger, cancellation, failure handling, crash recovery.

| Area | Implementation |
| --- | --- |
| Solution | `Relay.Core` (pure), `Relay.Windows` (Win32 wrappers), `Relay.Desktop` (WinUI 3, .NET 10), `Relay.Worker` (sandboxed console), `Relay.Gateway` (the one HTTP client), `Relay.Tests` (xUnit) |
| Hotkeys | `RegisterHotKey` on a dedicated message-only window thread; conflicts surfaced, never worked around |
| Capture | isolated `TextBox`; text accepted only from it; focus tracked; crash-safe draft every ≤200 ms |
| State | 12 states / 27 triggers in a pure `TransitionTable`; `SessionCoordinator` applies side effects after acceptance |
| Ledger | JSONL, SHA-256 chain, write-through + flush per record, verify/repair on start, `LOCKED` on tamper |
| Recovery | crash detection via session records, interrupted-draft Review item, torn-tail quarantine, interrupted turn/execution/worker detection |
| Failure | every exception → `FAILED` + incident file; unwritable ledger → `LOCKED`; single instance per data root |
| Flow | opt-in fixed-chord relay; off by default; every emission or skip logged |
| Security | data root ACL (user + SYSTEM), no hooks, no ports, no clipboard, no window titles in records |

Open before phase 1 is *closed*: a week against real Wispr Flow (stabilization defaults, F13/F14 choice, relay decision, contract §17); signed MSIX packaging.

## Phase 2 — Local records ✅

Built: `PathGuard` (canonicalization, reparse-point resolution, containment under registered roots), workspace registry, project registry with slugs and aliases, project folder layout (`project.toml`, `.orchestrator\`, `notes\`, `decisions\`, `tasks\`, `artifacts\`), note documents with front matter and `spans`, versioned writes (`.orchestrator\versions\`), archive with manifest, backup export + verification.

Exit met in tests: every project operation lands in the ledger; writes outside registered roots are refused by path tests (junctions, `..`, case tricks, device paths); archive/restore round-trips; backups verify.

## Phase 3 — Read-only orchestrator ✅ (rules) · 🟡 (model)

Built: `PLANNING` state; `IOrchestrator` with `TurnPlan` (summary, steps, answer, citations, proposals); read-only `ToolBroker` (`list_projects`, `search`, `read_note`, `project_notes`) with a per-turn budget; `RuleBasedOrchestrator` (deterministic grammar: create/archive/restore/rename project, list, remember, file the last note, recall, summarize, apply worker output, export backup); `CompositeOrchestrator` (rules first, model only when the grammar does not understand); `Relay.Gateway.OpenAiCompatibleClient` on one https endpoint with the key from DPAPI; `ModelOrchestrator` running a strict JSON tool loop in which citations are accepted only for ids a tool returned this turn and every proposal goes to policy like any other producer's.

Exit met in tests: every model round trip has `model.requested`/`model.responded` with sizes only; malformed model output ends the turn visibly with the raw text on record; the tool budget is enforced; there is no code path from a model reply to a write. Owed: a live run against a real endpoint (opt-in test exists: set `RELAY_LIVE_MODEL_KEY`), and a network-policy test that proves no other endpoint is opened (currently by construction: the gateway is the only `HttpClient` in the solution).

## Phase 4 — Controlled writes ✅

Built: `PolicyEngine` (tier table, target validation, path rules), proposals with hash-bound approvals, `AWAITING_APPROVAL`/`EXECUTING` states, edit-and-re-decide, single-use time-limited capabilities, `Executor` with typed operations and a write journal, journal recovery into Review after a crash, Stop during execution.

Exit met in tests: prohibited actions are denied on record; stale/edited approvals do not execute; a kill during `EXECUTING` leaves a Review item and no half-written file; user-initiated operations use the same path as orchestrator proposals.

## Phase 5 — Worker sandbox ✅

Built: `AgentRunSpec` (hashed input copies, allowlists, limits, staging), `WorkerBroker` (the only file access a worker has; every call allowed or denied on record), `Relay.Worker` (dependency-free console, JSON lines over stdio, `summarize` task), `WorkerRuntime` (wall clock, input-tamper check, required outputs, stop), `JobObjectWorkerHost` (kill-on-close, single process, memory cap, UI restrictions), `launch_worker` → `apply_patch` as two separate approvals.

Exit met in tests: a hostile worker's escapes are all denied and logged; a hanging worker is killed at the wall clock; a tampered run is rejected; the real `Relay.Worker.dll` completes a summary inside a job object. Owed: an explicit socket test (the worker has no network code and the job object allows no child processes; an outbound-connection block via WFP/AppContainer is the next hardening step).

## Phase 6 — Memory refinement ✅

Built: `NoteExtractor` (many typed notes per capture, each with narrow spans), `NoteRouter` (mentions, aliases, vocabulary overlap → auto / Review / unrouted, per-project threshold override), `DisputeDetector` + `supersede_note` without erasure, `ReviewStore` for pending routing and dispute decisions, `SearchIndex` over captures, drafts and project notes, recall that cites spans and marks superseded/disputed hits.

Exit met in tests: every filed note's span reproduces its exact characters from `capture.committed`; superseded notes stay on disk, stay indexed, and rank below current conclusions.

## Desktop UI 🟡

Built: status with orchestrator/model chips; capture with keyboard-only path (Type an instruction / Type a note); Response panel (summary, reasoning steps, answer, sources with ledger spans, proposals with tier/status/policy reasons, Approve/Edit/Reject/Approve all/Stop); Review for every decision kind (proposals, routing, disputes, interrupted turns/executions, incidents, settings); Projects (create/rename/archive/restore/open), Workspaces (register with folder picker/remove), Staging (file a draft under a project); Settings dialog (orchestrator mode, model endpoint/model/API key via DPAPI, workers, thresholds) applied on the next turn; Diagnostics.

Owed: a scripted UI Automation pass over the new regions (the phase-1 pass exists for capture flows), and screenshots for the README.

## Phase 7 — Additional integrations ⬜

One at a time, each with its own threat model and explicit enablement in settings. Candidates: file watcher for registered roots, calendar read (Tier A read only), export to a personal wiki. Nothing that sends, publishes, or purchases.

## Testing environment

`tests/Relay.Tests/Support/Scenario.cs` is a prompt-driven DSL over the real coordinator with a fixed clock, a manual scheduler, fakes for the window and Flow, and pluggable orchestrators: `Command("…")`, `Note("…")`, `Approve()`, `Reject()`, `Edit()`, `Cancel()`, `Restart()`, `CrashAndRestart()`, and `Expect*` assertions. Every scenario keeps a transcript (state, receipt, plan, proposals, ledger lines) that is printed on failure. `CannedOrchestrator` scripts plans; `ScriptedModelClient` scripts model replies so the JSON tool loop, citation checks and policy handling are tested without a network; `InProcessWorkerHost` runs the real worker protocol on the test thread; the real `Relay.Worker.dll` is also launched as a child process under a job object.

## Standing rules for every phase

- New behaviour = new ledger event types; never repurpose or rename.
- The `TransitionTable` stays pure and exhaustively tested; states are added, never overloaded.
- `Relay.Core` keeps zero UI, network, or P/Invoke dependencies (`Relay.Gateway` is the only project that opens a connection).
- No feature may hide work behind a spinner: if it takes time, it emits events.
- No permanent delete anywhere, ever, in this product line.
