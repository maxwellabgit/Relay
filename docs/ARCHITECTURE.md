# ARCHITECTURE.md

Authoritative runtime and data contracts for RELAY. Implementation details that contradict this document are defects. See also `docs/JEV_REFACTOR.md` for the Jev decision-engine target.

## Target flow

```text
Input adapters (composer · Wispr / ITranscriptSource · operation results)
  → Persist source object
  → Append case event · enqueue case
  → Context assembler (minimal state · deterministic candidates)
  → Bounded Jev question set (optional; grant-gated)
  → Persist judgment result
  → Deterministic decision engine (thresholds · capability registry)
  → Optional local text generation (schema-validated)
  → Feed item · child case · or operation envelope
  → Policy and operation broker (scope · approval · idempotency)
  → Append outcome evidence
  → Rebuildable projections (feed · tasks · memory · diagnostics)
```

## Authority split

- **Deterministic runtime** owns orchestration, transitions, policy, and side effects.
- **Jev** supplies typed Noul / Choice / Score judgments over supplied state only.
- **Local generator** drafts text after code selects the generation task; it does not choose moves, capabilities, or permissions.
- **No model returns an unconstrained next action** in production.

## One durable case

Everything the decision engine works on is a **case**:

```text
case
── id and monotonically increasing version
── origin: direct | observed | dialogue
── kind / capability binding
── approved objective (when applicable)
── source references
── ordered observations
── allowed capabilities
── budgets
── pending waits (including waiting_for_judgment)
── pending operation IDs
── processed event IDs
── judgment references
── result and completion criteria
└── presentation policy
```

Improvement is a **kind**, not an origin. An observed conversation may identify an improvement opportunity; activating it still requires explicit approval.

A listening stream is a long-lived observed case. A direct request is a case with a user objective. Interpreter, stepping, persistence, failure handling, and validation are identical; origin changes allowed capabilities, urgency, and presentation only.

Legacy `TaskLoop` and `ObservingLoop` are replaced by one `CaseRuntime`. Production routing based on `ICaseMind` / scripted minds is temporary migration scaffolding and must be removed at cutover in favor of `ICaseDecisionEngine`.

## Decision engine contract

Production dependency:

```csharp
Task<CaseDecision> DecideAsync(CaseDecisionRequest request, CancellationToken ct);
```

`CaseDecision` is an internal deterministic directive. Supported kinds include `wait`, `publish_feed`, `raise_case`, `request_generation`, `request_operation`, `complete`, and `no_action`.

Feed text is built from deterministic templates or capability results. It is never unconstrained model prose.

Question sets are immutable, versioned assets (`conversation.screen.v1`, `direct.route.v1`, `acronym.select.v1`, `note.support.v1`, …). Instructions are never assembled from transcript content. Multi-label detection uses one Noul per label.

## Judgments

- Typed request/response with Noul, Choice, and Score.
- Complete request/response bodies live in the content-addressed object store.
- Ledger, SQLite projections, and ordinary logs hold only IDs, hashes, question-set id/version, model, status, tokens, and latency.
- Cache by canonical request hash; failed results are not cached as success.
- Persist request before dispatch; persist response before applying the decision.
- Restart reuses completed judgments and requeues unresolved requests without double application.

## Hosted processing

Listening and hosted judgments are independent controls.

Every source object carries a classification: `local_only`, `hosted_allowed_session`, `hosted_allowed_project`, or `public`. Derived content inherits the most restrictive classification of its sources.

`DisclosurePolicy` verifies grant purpose/scope, classifications, and remaining token budget before any Jev request body is constructed. If any necessary source is excluded, the entire request is rejected.

## Operation envelope

Every side effect is bound by a single envelope with case version, capability, argument hashes, approval binding, and idempotency key. Approval binds the **canonical hash** of the envelope. Editing the payload creates a new version requiring a new policy decision.

## Storage layers

1. **Audit ledger** — immutable metadata describing what happened. No raw overheard conversation.
2. **Content-addressed object store** — transcript segments, judgment request/response bodies, prompts, external packages, tool packages, and other large or sensitive content. Referenced by ID and hash.
3. **Rebuildable SQLite projection** — cases, operations, approvals, feed items, judgments metadata, memory index, FTS search, and diagnostics. May be deleted and rebuilt from ledger + objects.

Files and event/object storage remain authoritative. During an enabled listening session, transcript segments must be written locally before they are marked ingested. Every derived note points to exact source segment IDs and character spans.

## Scheduler

Persistent ready queue. Suggested priority:

1. User reply or approval result
2. New direct request
3. Tool or judgment completion
4. Urgent observed conflict or commitment
5. Normal observed work
6. Maintenance and improvement reviews

A waiting observed case must not block a direct case. Judgment HTTP must not run under a process-wide lock that serializes unrelated cases.

## Local generator

OpenAI-compatible local endpoint for drafting only (`DraftNote`, `DraftTask`, `Answer`, `DraftClarification`). Strict JSON schema; validate before use. On outage: verbatim-source drafts for note/task; resumable waiting for direct answer.

## Research, tools, and workflows

Research brokers, tool builders, and workflow synthesis may remain in the tree for characterization, but are **not registered** in the v0.1 production capability registry. Do not claim a profile can search unless a real bound adapter exists and is intentionally enabled in a later release.

## UI coupling

The WinUI layer reads projections and submits commands through `IRelaySurface`. It must not own orchestration state. Production composition binds `CaseRuntime` / `CaseRuntimeSurface`, not legacy `SessionCoordinator` orchestration, after cutover.

## Diagnostics / local harness

Isolated local harness runs live under `.dev-runs/{run-id}/` with structured JSONL, a run manifest, optional focused payloads (dev profile only), crash bundles, and `replay-case.ps1`. See `dev/`. Jev scenarios must distinguish scripted, outage, privacy, improvement, and live (`SKIPPED` when no key) paths.

## Jev refactor phases

| Phase | Focus |
| --- | --- |
| 0 | Freeze baseline, characterize, `JEV_REFACTOR.md` |
| 1 | Correct product/architecture contract (this rewrite) |
| 2 | Judgment contracts + fake provider |
| 3 | TypeSafe HTTP gateway |
| 4 | Judgment persistence, cache, recovery |
| 5 | Hosted grants and disclosure |
| 6 | Decision engine replaces production mind moves |
| 7 | Four v0.1 capabilities |
| 8 | Scoped improvement lifecycle |
| 9 | Desktop bind to `CaseRuntimeSurface` |
| 10 | DevHarness + live gates |
| 11 | Delete obsolete production orchestration |

Historical Slice 1–7 characterization (scripted minds on `CaseRuntime`) remains valid evidence of the durable spine. It is not the v0.1 production intelligence contract.

Legacy code is retained until the replacement passes equivalent characterization tests. Do not delete for its own sake.
