# 03 · Security and permission model

Parent: `orchestrator_foundation_v0_1.md` §7, §8, §12. This document states the threat model, the trust boundaries, the three authority tiers, and exactly which protections exist in slice 1 versus later phases.

## 1. Assets and threats

Assets, in order of importance:

1. The **ledger** — the only record of what happened. Loss or silent alteration is the worst outcome.
2. **Captured text** — raw thoughts and instructions; private, sometimes sensitive.
3. **Project content** (*later*) — canonical notes, decisions, files.
4. **Credentials** (*later*) — model gateway keys.

Threats considered:

| Threat | Mitigation |
| --- | --- |
| Another local user or service reads Relay data | Data root ACL: inheritance disabled; current user and SYSTEM only (`storage.acl_applied`). BitLocker is assumed for at-rest protection of the disk. |
| Transcript text is inserted into the wrong application | Capture surface must be foreground and focused; focus loss is recorded and shown; Relay never redirects text. |
| Relay's synthetic key press reaches another application | The relay adapter can emit exactly one configured chord, only after verifying Relay's own surface is foreground; every emission or skip is a ledger record. |
| Flow uses surrounding text as context | The capture window shows no project data during capture; users are told to disable Flow Context Awareness for Relay. |
| Ledger tampered on disk | SHA-256 hash chain; verification on every start; break → `LOCKED` with the broken sequence number shown; new records continue from the real tail so the break stays evident. |
| Crash mid-write corrupts the ledger | Only a torn *last* line is tolerated; its bytes are quarantined, not discarded; `ledger.repaired` records it. |
| Two processes write the same ledger | Named mutex per data root; the ledger file is opened with an exclusive write handle. |
| Exceptions leave the UI lying about state | Every unhandled exception routes to `ReportFailure` → `FAILED` + incident file; if the coordinator itself is unavailable, an incident file is written and the process exits. |
| Model or worker mutates canonical data (*later*) | Models receive no filesystem or shell handle; typed proposals → policy engine → capability → executor. Workers write to staging only. |
| Secrets leak into ledger or prompts (*later*) | Secrets live in Windows Credential Manager / DPAPI; the ledger schema has no credential field; redaction is explicit per field, never heuristic. |

Out of scope for v0.1: a compromised OS account, kernel-level keyloggers, physical access with the disk unlocked.

## 2. Trust boundaries

```
┌──────────────────────── Relay.exe (one signed process, user integrity) ────────────────────────┐
│  Relay.Desktop (WinUI 3)   →  Relay.Core (pure state, ledger, storage)  ←  Relay.Windows (Win32) │
│  UI, capture TextBox           no UI / network / P-Invoke                  RegisterHotKey,       │
│  global exception handlers     owns every write                            SendInput (1 chord),  │
│                                                                            foreground, ACL,      │
│                                                                            single-instance       │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
        ▲ text via keyboard input only                          ▼ data root %LOCALAPPDATA%\Relay (ACL-restricted)
   Wispr Flow (untrusted peer process)                     ledger · staging · sessions · incidents · config
```

Later phases add, each behind its own boundary: a **model gateway adapter** (network egress allow-listed to one endpoint, sends only text the user captured in the current turn plus retrieved sources), a **privileged executor** over an ACL-restricted named pipe (the only component that writes canonical project files), and **worker sandboxes** (separate processes, staging directory only, no network by default).

## 3. Authority tiers

Deterministic code enforces these; a model may only *propose*.

### Tier A — automatic (no approval)

Implemented now:

- Append a ledger record.
- Write the crash-safe draft and draft notes into `staging\`.
- Move an interrupted draft into `staging\drafts\discarded\`.
- Read Relay's own data root.
- Apply the data-root ACL, register the two hotkeys, emit the one configured Flow chord.
- Perform local health checks (ledger verification, session liveness).

Later:

- Read the project registry and files under registered roots.
- Search indexes; produce proposals, plans, and diffs into staging.
- Add index entries derived from an authorized source.

### Tier B — requires explicit approval in the UI

None are possible in slice 1 (no code path exists). Later:

- Create a formal project folder; modify, rename, move, or archive a canonical note, decision, or artifact.
- Launch a worker; grant a worker network or more than one project.
- Apply a worker-produced patch.
- Export or transmit local content; add a tool or integration.

### Tier C — prohibited (no code path, and policy must reject if a proposal asks)

- Permanent deletion of any user content.
- Sending messages, publishing, deploying, purchasing, account changes.
- Arbitrary shell execution by the orchestrator or a worker.
- Reading outside registered workspace roots; reading clipboard, browser history, screen, email, calendar.
- Adding tools dynamically at a model's request.
- Workers writing to canonical storage.
- Relay emitting any synthetic key other than the configured Flow chord.

## 4. Action protocol (design for phase 3+, types land in `Relay.Core.Policy`)

```
model  ──proposal──▶  PolicyEngine.Validate  ──Decision──▶  UI (if approval)  ──Approval──▶  Executor
                       schema · action allow-list                 shows target,             short-lived capability
                       path canonicalization                      effects, diff             for exactly this proposal
                       state preconditions                                                  hash-bound to target+effects
                       tier lookup → requires_approval
```

Rules:

- Proposals are data (`proposal_id`, `action`, `reason`, `target`, `source_event_ids`, `expected_effects`, `risk`, `requires_approval`). The engine recomputes `requires_approval`; the model's own value is advisory only.
- Paths are canonicalized (`GetFullPath`, reparse-point resolution, case-fold) and must remain under a registered root; symlink/junction escape is rejected.
- An approval is bound to the SHA-256 of the canonical proposal. Any edit produces a new proposal and voids the old approval.
- Capabilities are single-use, expire in seconds, and name the exact operation. The executor accepts typed operations only (`CreateProjectFolder`, `WriteNoteVersion`, `MoveToArchive`…), never strings to interpret.
- Every proposal, decision, approval, execution, and outcome is a ledger record linked by `proposal_id`.
- Replay after crash re-executes nothing that has an `execution.completed` record; anything with `execution.started` but no completion is surfaced in Review, not retried automatically.

## 5. Process and data protections in slice 1

| Protection | Where |
| --- | --- |
| Single process, no localhost port, no embedded browser | `Relay.Desktop` |
| Unpackaged WinUI 3 with self-contained Windows App SDK for local; MSIX + signing for release | `Relay.Desktop.csproj`; see build plan |
| Data root ACL hardened on every start; failure is recorded and shown, not fatal | `Relay.Windows/DirectoryAcl.cs` |
| Ledger: append-only stream, `FileShare.Read` for readers, explicit `Flush(flushToDisk: true)` per record | `Relay.Core/Ledger/FileLedger.cs` |
| Atomic file replacement for drafts, notes, settings, sessions (`write temp → flush → File.Replace/Move`) | `Relay.Core/Storage/AtomicFile.cs` |
| Hash chain verification on every start; `LOCKED` on break | `LedgerVerifier`, `SessionCoordinator.Start` |
| Incident files hold exception type, message, and stack — never capture text; drafts store text because they *are* the crash-safety copy | `SessionCoordinator.WriteIncidentFile`, `CaptureDraft` |
| Foreground process **name** recorded, never window titles (titles can contain document contents) | `ForegroundWindows.ForegroundProcessName` |
| Global hotkeys via `RegisterHotKey` on a message-only window; no `WH_KEYBOARD_LL` hook | `Relay.Windows/HotkeyListener.cs` |
| Settings validation with per-section fallback; invalid values surface in Review | `SettingsStore.Load` |

## 6. Residual risks accepted for slice 1

- Local builds are unsigned and unpackaged. Nothing prevents a same-user process from modifying `Relay.exe`; this is addressed by MSIX signing in phase 1b.
- `SendInput` of the Flow chord is a synthetic keypress and Flow could theoretically change its binding; the adapter is off by default and each emission is logged.
- The ACL protects against other *users*; any process running as the same user can read the data root. Application-level encryption is deferred (contract §17.4).
