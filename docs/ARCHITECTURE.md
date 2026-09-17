# ARCHITECTURE.md

Authoritative runtime and data contracts for RELAY. Implementation details that contradict this document are defects.

## Target flow

```text
Input adapters (composer · Wispr Flow · operation results)
  → Event intake (validate · store · correlate)
  → Persistent case runtime (ready queue · version · waits · budgets)
  → Context assembler (case events · memory · capabilities)
  → One local mind (one constrained move)
  → Move validator (schema · ids · available actions)
  → Policy and operation broker (scope · approval · idempotency)
  → Tools · workflows · search · delegates · executor
  → back to Event intake
  → Rebuildable projections (feed · tasks · memory · diagnostics)
```

## One durable case

Everything the mind works on is a **case**:

```text
case
── id and monotonically increasing version
── origin: direct | observed | dialogue
── kind: remember | check | resolve | answer | organize | research | improve
── approved objective
── source references
── ordered observations
── allowed capabilities
── budgets
── pending waits
── pending operation IDs
── processed event IDs
── result and completion criteria
└── presentation policy
```

Improvement is a **kind**, not an origin. An observed conversation may identify an improvement opportunity; building or activating it still requires explicit approval.

A listening stream is a long-lived case whose allowed moves are limited to read-only checks, `raise_task`, `say`, and `wait`. A direct request is a case with a user objective and broader possible moves. Interpreter, stepping, persistence, failure handling, and move validation are identical.

Legacy `TaskLoop` and `ObservingLoop` are replaced by one `CaseRuntime` (also called DecisionLoop in design notes). They must not drift.

## One model-step contract

Grammar-constrained generation (llama.cpp) is an **output constraint only**, not a product architecture.

A step returns:

```json
{
  "read": {
    "intent": "one sentence",
    "significance": 0.0,
    "urgency": 0.0,
    "sensitivity": 0.0,
    "confidence": {
      "interpretation": 0.0,
      "evidence": 0.0,
      "utility": 0.0
    },
    "needs": []
  },
  "move": {
    "type": "say | use_tool | propose | delegate | build | run_workflow | ask_user | raise_task | wait | stop",
    "name": "",
    "text": "",
    "args": {},
    "done": false
  },
  "feed": "One concise sentence."
}
```

The runtime supplies allowed moves and available capabilities for that case. An observation case may use `raise_task`; an ordinary task may not.

## Operation envelope

Every side effect is bound by a single envelope:

```json
{
  "operationId": "01...",
  "caseId": "01...",
  "caseVersion": 17,
  "causedByEventId": "01...",
  "capability": "project.note.modify",
  "capabilityVersion": 1,
  "arguments": {},
  "inputRefs": [
    {
      "objectId": "01...",
      "version": 3,
      "selector": "body",
      "sha256": "..."
    }
  ],
  "requestedScope": {},
  "grantedScope": {},
  "approvalId": "01...",
  "idempotencyKey": "...",
  "preconditions": [],
  "status": "requested",
  "resultRef": null
}
```

Approval binds the **canonical hash** of this envelope. Editing the payload creates a new version requiring a new policy decision.

## Storage layers

1. **Audit ledger** — immutable metadata describing what happened. No raw overheard conversation.
2. **Content-addressed object store** — transcript segments, focused prompts, external packages, responses, fetched pages, tool packages, and other large or sensitive content. Referenced by ID and hash.
3. **Rebuildable SQLite projection** — cases, operations, approvals, feed items, memory index, FTS search, and diagnostics. May be deleted and rebuilt from ledger + objects.

Files and event/object storage remain authoritative. During an enabled listening session, transcript segments must be written locally before they are marked ingested.

Every derived note points to exact source segment IDs and character spans.

## Scheduler

Replace immediate fire-and-forget queuing with a **persistent ready queue**. Suggested priority:

1. User reply or approval result
2. New direct request
3. Tool or delegate completion
4. Urgent observed conflict or commitment
5. Normal observed work
6. Maintenance and improvement reviews

Only local inference needs global serialization (one lease). Tools, search, external requests, and sandbox work may run concurrently under individual limits.

## Research path

1. Local mind proposes online research.
2. Permission broker authorizes a search scope.
3. Deterministic search adapter returns hits.
4. Allow-listed fetch adapter retrieves selected pages.
5. Full source artifacts are stored with hashes and timestamps.
6. Delegate receives those exact artifacts.
7. Complete delegate response is stored.
8. Local mind interprets the response against the objective.
9. Final claims cite stored source artifacts, not merely the delegate.

Do not claim a profile can search unless a real bound adapter exists.

## Tools and workflows

Tool creation is two stages: (1) generalize the repeated capability, (2) draft a package with a neutral name, typed I/O, host capabilities, and tests that include inputs not supplied by the original request.

Workflow “testing” must execute against fixtures (value flow, wait/resume, permissions, failure handling, acceptance), not only structural field presence.

## UI coupling

The WinUI layer reads projections and submits commands. It must not own orchestration state.

## Diagnostics / local harness

Isolated local harness runs live under `.dev-runs/{run-id}/` with structured JSONL (`runtime.jsonl`, `model.jsonl`, `ui.jsonl`), a run manifest, optional focused payloads (dev profile only), crash bundles, and `replay-case.ps1`. See `dev/`.

## Build slices (order)

| Slice | Focus | Exit |
| --- | --- | --- |
| 0 | Truthful ground: archive, three docs, retention ledger, characterization | No judge/planner/grammar-first ambiguity in product contract |
| 1 | Durable case runtime + harness | Restart through approval; duplicate completion is idempotent |
| 2 | Direct vertical path | Atlas beta date recall with citations |
| 3 | Listening via same pipeline | Source-linked notes, acronyms, concurrent direct ask |
| 4 | Real research + delegation | Lightshift evidence path |
| 5 | Generalized tools + fixture workflows | `world_clock` reused for Kathmandu/London |
| 6 | UI reads projections only | One feed + one composer drive core scenarios |
| 7 | Measured personalization | Typed improvement proposals with evaluation and reversion |

Legacy code is retained until the replacement passes equivalent characterization tests. Do not delete for its own sake.
