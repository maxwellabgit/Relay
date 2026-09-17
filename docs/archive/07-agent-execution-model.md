# 07 · Agent execution model

Nothing in this document is implemented in slice 1. It exists so that every phase 3–5 decision is made against a fixed model rather than improvised, and so that slice 1 leaves the right seams (`RelayState.Planning/AwaitingApproval/Executing`, `spans`, `staging\`, the ledger envelope).

## 1. Two kinds of agent

| | Orchestrator | Worker |
| --- | --- | --- |
| Identity | one, durable, the only agent with memory | many, temporary, none |
| Memory | project registry, notes, decisions, ledger-backed recall | the input bundle only |
| Lifetime | the Relay process | one task; process killed at completion or limit |
| Where it runs | inside Relay, calling a model gateway adapter | separate process, job object, low integrity, staging directory as cwd |
| Can write | proposals into staging; nothing canonical | its own staging directory only |
| Network | model gateway endpoint only | none unless separately approved for that run |
| Sees | captured text of the current turn, retrieved sources with spans, registry summaries | its input bundle (copies), never the ledger, registry, or credentials |

## 2. Orchestrator turn (command mode after slice 1)

```
COMMAND_CAPTURE → AWAITING_TRANSCRIPT → ORGANIZING            (slice 1: capture.committed, command.recorded)
  → PLANNING          model call: instruction + retrieved sources → plan + proposals   (visible: "Reading 3 sources", "Drafting plan")
  → AWAITING_APPROVAL if any proposal is Tier B                                        (Review shows each proposal, target, effects, diff)
  → EXECUTING         executor runs approved + Tier A operations in order              (Activity: one line per operation, start and end)
  → COMPLETED | FAILED
```

Visible-thinking rule: the model's plan and each tool call's *input summary and result* are Activity entries with ledger records (`plan.proposed`, `tool.called{name, argsSummary}`, `tool.returned{ok, summary}`). Raw model output is stored as an event; the UI shows a summary that links to it. No spinner ever stands in for these lines.

Recall answers (Tier A) cite `noteId` + `spans`; the UI resolves spans to the exact captured sentence.

## 3. Proposal → execution pipeline

1. **Proposal** (model output, validated as JSON schema):
   `{proposal_id, action, reason, target, source_event_ids[], expected_effects[], risk, requires_approval}`
2. **Policy engine** (`Relay.Core.Policy`, pure): action allow-list → tier; target canonicalization and root check; state preconditions (e.g. project not archived); recomputes `requires_approval`; emits `Decision{allow|needs_approval|deny, reasons[]}`. Ledger: `proposal.received`, `proposal.decided`.
3. **Approval** (UI): the user sees the exact target and effects, and for file changes the full diff. Approve / Edit (creates a new proposal) / Reject. Ledger: `approval.granted{proposalHash}` or `approval.rejected`.
4. **Capability**: `{proposal_id, proposalHash, operation, expiresAt}` signed with a per-session key held only by the executor side; single use.
5. **Executor** (initially in-process, later a separate process over an ACL-restricted named pipe): accepts typed operations only, verifies the capability hash matches the proposal it is about to execute, performs the write atomically, records `execution.started` → `execution.completed{resultHashes}` or `execution.failed`.
6. **Recovery**: on restart, `execution.started` without `execution.completed` is shown in Review with the on-disk state; nothing is re-run automatically.

## 4. Worker contract (phase 5)

A worker launch is a Tier B proposal with a fully specified `agent_run`:

```json
{
  "runId": "01M1…", "taskId": "…", "objective": "Summarize the three market notes into a comparison table",
  "inputs": [ { "path": "notes/2026-09-01-pricing.md", "sha256": "…" } ],
  "fileAllowlist": { "read": ["inputs/**"], "write": ["out/**"] },
  "toolAllowlist": ["read_file", "write_file", "list_dir"],
  "network": false,
  "limits": { "wallClockSec": 300, "tokens": 60000, "costUsd": 0.50 },
  "staging": "%LOCALAPPDATA%\\Relay\\staging\\agents\\01M1…\\",
  "requiredOutputs": ["out/report.md"],
  "verification": ["out/report.md exists", "no writes outside out/"]
}
```

Enforcement, all deterministic:

- The worker process is started by the executor with: `cwd` = its staging directory; inputs *copied* (never linked) into `inputs\` read-only; a Windows job object with memory/CPU/time limits and kill-on-close; a restricted token (no network via Windows Filtering Platform rule or AppContainer once packaged); no environment variables containing credentials.
- Tool calls from the worker go through a broker in Relay that checks `fileAllowlist` and `toolAllowlist` per call and records `agent.tool_called`.
- Output lands in `out\`. The orchestrator reads it, validates required outputs and verification criteria, presents a summary and diff, and requests approval to *apply* — applying is a separate Tier B proposal handled by the executor.
- Exceeding any limit kills the run: `agent_run.terminated{reason}`. Partial output stays in staging for inspection.
- Workers never see the ledger, the registry, other projects, or the model gateway key; they get their own short-lived model credential scoped to the run if they need a model at all.

## 5. Ledger events added in these phases

`plan.proposed`, `proposal.received`, `proposal.decided`, `approval.granted`, `approval.rejected`, `execution.started`, `execution.completed`, `execution.failed`, `tool.called`, `tool.returned`, `agent_run.launched`, `agent_run.tool_called`, `agent_run.completed`, `agent_run.terminated`, `patch.applied`, `note.routed`, `note.extracted`, `project.created`, `project.moved`, `project.archived`. Same envelope as slice 1; same hash chain.

## 6. Non-goals

No autonomous background work: the orchestrator acts only inside a command turn or an explicitly scheduled, user-approved job. No agent-to-agent messaging outside the broker. No dynamic tool registration.
