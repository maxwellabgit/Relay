# PRODUCT.md

Authoritative product contract for RELAY. Historical documents live under `docs/archive/` and are not current specification. The Jev decision-engine target is detailed in `docs/JEV_REFACTOR.md`.

## What RELAY is

RELAY is a **local personal conversation-to-action learner**. It is not primarily a chatbot, dictation app, note taker, or autonomous agent that writes code or decides its own permissions.

It continuously accepts two kinds of input:

- **Explicit input** typed or dictated directly to RELAY (`direct`).
- **Enabled conversation streams** supplied through Wispr Flow or another `ITranscriptSource` (`observed`). Transcripts arrive already transcribed; RELAY does not capture or diarize audio in v0.1.

Everything enters the **same event-driven case runtime**. Origin changes authorization, urgency, presentation, and allowed capabilities. Origin does **not** select a different pipeline.

## Authority split

| Layer | Owns |
| --- | --- |
| Deterministic runtime | Case state, event ordering, scheduling, candidate extraction, retrieval, capability availability, policy, permissions, approvals, budgets, idempotency, retries, side effects, recovery, canonical writes |
| Jev (TypeSafe System One) | Narrow hosted semantic judgments over supplied text/JSON state — Noul, Choice, Score only. Optional per session/project grant. |
| Local generator | Drafting a note, task title, short answer, or clarification **after** code has selected the generation task |
| User | Hosted-processing grants, external disclosure, canonical modifications, new standing grants, improvement activation |

No model is an authority. Jev probability or confidence never grants permission. The local generator is not semantic routing authority and must never silently substitute for Jev.

Jev is text-only, schema-bounded, and not a chatbot, planner, or free-form text generator. “Zero hallucinations” means it cannot invent answers outside the supplied schema; it can still make an incorrect typed decision.

## v0.1 scope

Exactly four enabled capabilities:

1. `conversation.note.capture` — source-linked notes from observed or direct input
2. `conversation.task.capture` — local task proposals with explicit owner/date candidates only
3. `glossary.acronym.resolve` — deterministic glossary lookup, Jev only when ambiguous
4. `direct.answer` — local answers over approved local evidence, with citations

Exactly one enabled improvement family: project-scoped glossary or filing/presentation preference changes from repeated equivalent corrections (or one explicit direct instruction), evaluated, approval-bound, shadowed, measurable, and reversible.

Deliberately deferred: research/delegation, generated tools, workflow synthesis, third-party connectors, in-app STT/diarization, fine-tuning, automatic permission or improvement activation, and any local-model fallback for Jev judgments.

## Personalization

RELAY becomes personal by accumulating durable memory and preferences under measured improvement. Fine-tuning the local model may happen later; it is **not** the initial personalization mechanism.

The intended result is a quiet system that:

- Remembers relevant decisions with sources.
- Detects commitments, corrections, and unresolved terms.
- Answers direct questions while still listening.
- Proposes scoped glossary/preference improvements when friction repeats.
- Never silently expands its permissions or objective.

## Primary surface

There is one primary surface:

- A **Listening** toggle.
- A separate **Allow hosted judgments** session toggle (independent of listening).
- One chronological **feed**.
- One **composer** at the bottom.
- Inline approval and clarification cards.
- Expandable evidence for every note, task, correction, or answer.
- Status for transcript capture, Jev availability, local generator availability, waiting cases, and pending approvals.
- Cost and token display for each judgment (and generation) operation.

There are no separate “agent,” “planner,” “judge,” or “command” boxes. Listening and direct asking are input conditions, not separate orchestrator modes.

### Attention levels

| Presentation | Behavior |
| --- | --- |
| Ambient | A subtle saved note, connection, or minor observation |
| Persistent | Acronym meaning, commitment, unresolved question, or useful connection |
| Alert | A likely contradiction, approaching deadline, or significant correction |
| Proposal | An editable operation that requires permission |
| Finding | A completed answer with evidence |

Activity tags must be derived from **enforced runtime state**, not model-written decoration. Approvals suspend only the affected operation. They must never freeze listening or unrelated work.

## Invariants

1. **One durable case runtime and one deterministic decision engine.** Direct asks and observed streams share persistence, queue, policy, and operation broker. The engine may call Jev or the local generator; code owns transitions and the available action set.
2. **Every side effect passes through an operation envelope** with case version, scope, approval binding, and idempotency key.
3. **Persist before consequence.** Case events are written before their effects are dispatched. Recovery rebuilds exclusively from those events.
4. **Clean shutdown is suspension.** Restart reconstructs pending cases; it does not discard listening buffers or in-flight work that was durably recorded.
5. **Late or duplicate completions do not repeat side effects.** Cancelled or revised cases store stale results without mutating the case.
6. **Observed authorization is narrower.** An observed conversation may read allowed local sources and propose work. It may not send, delete, change canonical files, or contact an external service without an applicable grant or approval.
7. **Privacy.** Raw overheard words never appear in the audit ledger, ordinary logs, approval hashes, or error strings. Hosted disclosure requires an applicable grant; `local_only` sources block the entire judgment request.
8. **No silent permission expansion.** Changing the objective, deliverable, material scope, or consequence requires a versioned revision and approval.
9. **Jev-unavailable work waits.** Capture and deterministic lookups continue; new semantic work enters durable `waiting_for_judgment` and resumes without duplication. The local generator is never an undeclared judgment fallback.

## Out of scope until v0.1 is stable

Gmail, Slack, calendar, live web research, generated tools, and broader workflow synthesis remain outside production registration until the conversation-to-action slice, hosted-judgment controls, and one improvement family are genuinely stable.
