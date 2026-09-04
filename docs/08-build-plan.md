# 08 · Incremental build plan

Each phase ships only when its exit criteria hold and the previous phase's ledger, state machine, and tests are unchanged except by addition. Intelligence is added last to the parts that can do the least harm.

## Phase 1 — Control surface (this repository, slice 1) ✅ built and verified

Scope: two hotkeys, isolated capture, visible state, append-only hash-chained ledger, cancellation, failure handling, crash recovery. No model, no project mutation, no integrations, no workers.

Delivered:

| Area | Implementation |
| --- | --- |
| Solution | `Relay.Core` (pure), `Relay.Windows` (Win32 wrappers), `Relay.Desktop` (WinUI 3, .NET 10), `Relay.Tests` (xUnit) |
| Hotkeys | `RegisterHotKey` on a dedicated message-only window thread; conflicts surfaced, never worked around |
| Capture | isolated `TextBox`; text accepted only from it; focus tracked; crash-safe draft every ≤200 ms |
| State | 12 states / 17 triggers in a pure `TransitionTable`; `SessionCoordinator` applies side effects after acceptance |
| Ledger | JSONL, SHA-256 chain, write-through + flush per record, verify/repair on start, `LOCKED` on tamper |
| Recovery | crash detection via session records, interrupted-draft Review item (Commit / Discard), torn-tail quarantine, redundant-draft detection |
| Failure | every exception → `FAILED` + incident file; unwritable ledger → `LOCKED`; single instance per data root |
| Flow | opt-in fixed-chord relay; off by default; every emission or skip logged |
| Security | data root ACL (user + SYSTEM), no hooks, no ports, no clipboard, no window titles in records |
| UI | Status / Capture / Activity / Review regions + Diagnostics; state label never colour-only |

Verified live (see README for the procedure): note capture end to end, command capture with *Ready*, other-key rejection, Esc cancel with Recover/Forget, `taskkill` mid-capture → interrupted draft recovered and committed, second instance exits, clean shutdown writes `session.ended`. Unit tests: ledger encode/verify/repair/tamper, every transition, capture flows, timeouts, retry idempotency, recovery.

Exit criteria remaining before phase 1 is *closed*:

- [ ] Run against real Wispr Flow for a week; confirm stabilization defaults; confirm F13/F14 or pick alternatives (contract §17.1).
- [ ] Decide whether the fixed-chord relay is acceptable (§17.3); if yes, enable by default with first-run guidance.
- [ ] Package as MSIX with a development certificate; add `AppContainer`-compatible paths check. (Unpackaged self-contained build is used now so `dotnet build` works without Visual Studio.)
- [ ] Sign release builds; disable auto-update until an update threat model exists.

## Phase 2 — Local records

Adds: project registry (`registry\projects.jsonl`), project folder creation **by the user through the UI** (still no model), typed note files with front matter and `spans`, promotion of draft notes to a project by explicit user choice, `.orchestrator\` metadata and versioned writes, backup export (zip of data root + registered roots with manifest hashes), and restore verification.

Exit: create/rename/archive a project from the UI with every step in the ledger; corrupt the registry and recover from backup; zero writes outside registered roots proven by tests that assert on path canonicalization.

## Phase 3 — Read-only orchestrator

Adds: `PLANNING` state, model gateway adapter (one endpoint, allow-listed; user chooses the endpoint per §17.5), prompt assembly from *only* the current instruction + retrieved sources, visible plan and tool calls in Activity, search over notes and transcripts (exact + metadata; semantic index optional and marked non-authoritative), recall with citations.

Exit: every model call has `tool.called`/`tool.returned` records; no code path from a model response to a file write exists; network policy test proves the process opens no other endpoints.

## Phase 4 — Controlled writes

Adds: `Relay.Core.Policy` (proposal schema, tier table, path rules), `AWAITING_APPROVAL` and `EXECUTING` states, in-process executor with typed operations and capabilities, diff view in Review, edit-and-re-propose flow, execution recovery in Review.

Exit: fuzzed proposals (bad paths, junctions, case tricks, stale approvals) are all denied by tests; an approved change applied twice is idempotent; kill during `EXECUTING` leaves a Review item and no half-written file.

## Phase 5 — Worker sandbox

Adds: one worker type (local summarizer/patch producer), job-object sandbox, tool broker, staging-only output, apply-as-proposal. Then, only if needed, the separate privileged executor process over a named pipe.

Exit: a worker that tries to read outside its inputs, write outside `out\`, or open a socket is terminated and the attempt is in the ledger.

## Phase 6 — Memory refinement

Adds: note extraction (many notes per capture, each with narrow spans), routing confidence with Review for low confidence, contradiction detection (`disputed`), supersession without erasure, recall that always cites spans.

Exit: every note on disk resolves to exact characters in a `capture.committed` record; a superseded note is still on disk and still cited when relevant.

## Phase 7 — Additional integrations

One at a time, each with its own threat model and explicit enablement in settings. Candidates: file watcher for registered roots, calendar read (Tier A read only), export to a personal wiki. Nothing that sends, publishes, or purchases.

## Standing rules for every phase

- New behaviour = new ledger event types; never repurpose or rename.
- The `TransitionTable` stays pure and exhaustively tested; states are added, never overloaded.
- `Relay.Core` keeps zero UI, network, or P/Invoke dependencies.
- No feature may hide work behind a spinner: if it takes time, it emits events.
- No permanent delete anywhere, ever, in this product line.
