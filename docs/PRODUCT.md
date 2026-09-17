# PRODUCT.md

Authoritative product contract for RELAY. Historical documents live under `docs/archive/` and are not current specification.

## What RELAY is

RELAY is a **local personal workflow orchestrator**. It is not primarily a chatbot, dictation app, note taker, or autonomous agent.

It continuously accepts two kinds of input:

- **Explicit input** typed or dictated directly to RELAY.
- **Enabled conversation streams** supplied through Wispr Flow (or any equivalent Windows dictation surface).

Everything enters the **same event-driven runtime**. Origin changes authorization, urgency, and presentation. Origin does **not** select a different intelligence pipeline.

## Authority split

A small local model (initially Ministral 8B Q4) is the **single semantic judge**. It determines:

- What the user means.
- Whether something matters.
- What existing context is relevant.
- Whether a note, answer, check, task, proposal, tool, workflow, or outside worker would help.
- What should happen next within the currently authorized objective.

The model never becomes the authority. Deterministic software owns:

- Task / case state
- Scheduling
- Retrieval
- Permissions
- Approvals
- Budgets
- Tool validation
- Execution
- Idempotency
- Recovery
- Audit records
- Canonical writes

## Personalization

RELAY becomes personal by accumulating durable memory, preferences, approved tools, and reusable workflows. Fine-tuning the local model may happen later; it is **not** the initial personalization mechanism.

The intended result is a quiet system that gradually removes repeated coordination work:

- Remembers relevant decisions with sources.
- Detects contradictions and unresolved commitments.
- Answers direct questions while still listening.
- Delegates bounded research without blocking the rest of the application.
- Proposes improvements when it notices repeated friction.
- Builds and tests personal tools or workflows only after authorization.
- Never silently expands its permissions or objective.

## Primary surface

There is one primary surface:

- A **Listening** toggle at the top.
- One chronological **feed**.
- One **composer** at the bottom.
- Inline approval and clarification cards.
- Expandable evidence.
- A small pending-approval indicator.
- Secondary drawers for projects, tasks, memory, activity, and settings.

There are no separate “agent,” “planner,” “judge,” or “command” boxes. Listening and direct asking are input conditions, not separate orchestrator modes.

### Attention levels

| Presentation | Behavior |
| --- | --- |
| Ambient | A subtle saved note, connection, or minor observation |
| Persistent | Acronym meaning, commitment, unresolved question, or useful connection |
| Alert | A likely contradiction, approaching deadline, or significant correction |
| Proposal | An editable operation that requires permission |
| Finding | A completed answer or research result with evidence |

Activity tags must be derived from **enforced runtime state**, not model-written decoration. `Searching Online` means the network broker actually granted a search. `Asking GPT-5 nano` means a real external request with a stored package and budget.

Approvals suspend only the affected operation. They must never freeze listening or unrelated work.

## Invariants

1. **One local mind, one decision loop.** Direct asks and observed streams are cases with different allowed moves, not different runtimes.
2. **Every side effect passes through an operation envelope** with case version, scope, approval binding, and idempotency key.
3. **Persist before consequence.** Case events are written before their effects are dispatched. Recovery rebuilds exclusively from those events.
4. **Clean shutdown is suspension.** Restart reconstructs pending cases; it does not discard listening buffers or in-flight work that was durably recorded.
5. **Late or duplicate completions do not repeat side effects.** Cancelled or revised cases store stale results without mutating the case.
6. **Observed authorization is narrower.** An observed conversation may read allowed local sources and propose work. It may not send, delete, change canonical files, or contact an external service without an applicable grant or approval.
7. **Privacy.** Raw overheard words never appear in the audit ledger, ordinary logs, approval hashes, or error strings.
8. **No silent permission expansion.** Changing the objective, deliverable, material scope, or consequence requires a versioned revision and approval.

## Out of scope until core is stable

Gmail and other third-party integrations remain outside the build until the durable runtime, research path, tool generalization, and one-feed surface are genuinely stable.
