# JEV-REFACTOR.md

Architectural refactor: replace the production “read → move → feed” mind contract with
explicit controllers that ask narrow Jev questions and combine answers in code.

Software owns the workflow. Jev evaluates narrowly defined properties. The local model
runs bounded interpretation and generation jobs only.

## Starting point

| Field | Value |
| --- | --- |
| Repository | maxwellabgit/Relay |
| Inspected / starting SHA | `55551224b7a7fb5006f752d1015a9937aeea4f10` |
| Branch | `refactor/jev-runtime` |
| Intervening commits since inspection | None — HEAD matched the inspected SHA |
| .NET SDK (pinned) | `10.0.401` via `global.json` |

## Product statement (preserved)

RELAY turns ongoing work into durable context and approved, reusable capabilities, so it
can complete more of your recurring work with less direction over time.

## Settled requirements preserved

- Persistent cases can span conversations; a case does not itself authorize an objective.
- Listening and hosted processing are independently controlled.
- Jev is the semantic judgment provider; the local model performs interpretation/generation.
- During Jev outages: continue capture, permitted retrieval, and already determined operations; queue semantic decisions.
- Local-only restrictions propagate through derived content.
- Corrections become source-linked tentative understanding; canonical changes need authorization.
- Conflicting speakers remain a conflict unless evidence or recorded authority resolves it.
- Transcripts retained locally 30 days by default; approved supporting excerpts preserved separately.
- At most two additional resolution rounds per decision.
- Prepare improvement proposals automatically; build under a grant; activate only after explicit approval.
- Personal memory, workflows, decision definitions, and permissions survive model replacement.

## What must change (from inspection)

| Component | Required change |
| --- | --- |
| `CaseRuntime.StepCaseAsync` | Remove synchronous model execution inside the shared lock |
| `CaseRuntime.ExecuteOperation` | Separate dispatch from result ingestion; prevent dispatch after cancellation |
| `ICaseMind` / `CaseMindStep` | Replace as production orchestration interface; retain temporarily for historical tests |
| `ListeningScriptedMind` | Replace keyword logic and in-memory handled-segment tracking |
| `MarkListeningSegmentsHandled` | Remove fallback that marks the first pending segment without explicit coverage |
| `StreamIntake` | Durable windows, processing coverage, independent capture/processing state |
| `ResearchBroker` / `DelegateRequest` | Supply permitted source content, not only artifact IDs/metadata |
| Desktop composition | Bind to `IRelaySurface`; remove production `SessionCoordinator` dependency |
| Capability improvement | Versioned decision definitions, executable evaluations, activation approval, rollback |

## Target components

| Component | Responsibility |
| --- | --- |
| `CaseController` | Deterministic transition from persisted case state + input → events/commands |
| `DecisionCatalog` | Versioned atomic questions, state requirements, interpretation rules |
| `ContextAssembler` | Smallest useful state for a particular decision |
| `JudgmentDispatcher` | Authorize/dispatch Jev requests; persist results and usage |
| `LocalJobDispatcher` | Bounded local interpretation/generation jobs |
| `DecisionPolicy` | Combine judgments into explicit application behavior |
| `OperationDispatcher` | Authorized tools/research/builds asynchronously |
| `EvidenceStore` | Statements, interpretations, facts, conflicts, provenance, expiry |
| `CapabilityRegistry` | Active tools, workflows, worker profiles, applicability |

Production interface: `ICaseController.Handle(CaseSnapshot, CaseInput) → CaseTransition`.
`Handle` performs no HTTP, model inference, file mutation, or tool execution.

## Implementation sequence

1. Baseline (this document + VERIFICATION + JEV-DECISIONS) — done
2. Persistence and asynchronous execution (controller, outbox, cancel/dispatch split) — current
3. Provenance and hosted authorization
4. Real Jev transport with strict fixtures
5. Atomic decisions composed in code
6. Context retrieval and local jobs
7. Durable listening windows
8. Production workflows (recall, plan impact, research, improvement)
9. Retention and capability bundles
10. Desktop binding and diagnostics
11. Cloud verification scripts and named harness scenarios

## Explicit non-goals / corrections vs prior vNext mind

- Do **not** implement a Jev-backed `ICaseMind` that returns the old move contract.
- Do **not** expose a universal `choose_next_action` Jev question.
- Do **not** silently replace Jev with a local judgment provider during outages.
- Do **not** claim universal exactly-once execution.
