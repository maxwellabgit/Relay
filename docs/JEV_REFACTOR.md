# JEV_REFACTOR.md

Implementation architecture for **RELAY Conversation-to-Action v0.1**: a conversation-to-action learner that listens to approved transcript streams, identifies useful events with Jev, turns them into source-linked notes/tasks/lookups, and improves a small set of project-specific preferences through measured, reversible changes.

This document is the authoritative target for the Jev decision-engine refactor. It does not claim the work is complete — see `STATUS.md`.

| Field | Value |
| --- | --- |
| Baseline commit | `55551224b7a7fb5006f752d1015a9937aeea4f10` |
| Baseline tag | `pre-jev-refactor-55551224` |
| Working branch | `refactor/jev-decision-engine` |
| Related prior work | `origin/refactor/jev-runtime` — **reference only** (superseded; do not merge) |

---

## Product boundary (v0.1)

RELAY is **not** an autonomous agent that writes new code or decides its own permissions.

### Enabled capabilities

1. `conversation.note.capture@1`
2. `conversation.task.capture@1`
3. `glossary.acronym.resolve@1`
4. `direct.answer@1`

### Enabled improvement family

- Project-scoped glossary or preference changes from repeated, semantically equivalent corrections (or one explicit direct instruction).

### Deliberately not production-enabled

- Arbitrary generated code or self-editing
- Automatic permission changes or automatic improvement activation
- Gmail, Slack, calendar, or other third-party connectors
- Live web research or reasoning-model delegation
- Generated tool promotion / general workflow synthesis
- In-app audio capture, speech-to-text, or diarization (transcripts arrive already transcribed)
- Fine-tuning or changing model weights
- A local-model fallback that silently substitutes for Jev

Research and tool/workflow infrastructure may remain compiled and tested, but must not be registered in the production capability registry for this release.

### Primary surface

- `Listening` toggle
- Separate `Allow hosted judgments` session toggle
- One chronological feed
- One composer for typed or dictated direct requests
- Inline approval/clarification cards
- Expandable evidence for every note, task, correction, or answer
- Status for transcript capture, Jev availability, local generator availability, waiting cases, and pending approvals
- Cost and token display for each Jev (and generation) operation

Inputs: `observed` (Wispr Flow / `ITranscriptSource`) and `direct` (composer).

---

## Authority split

| Layer | Owns |
| --- | --- |
| Deterministic runtime | Case state, event ordering, scheduling, candidate extraction, retrieval, capability availability, policy, permissions, approvals, budgets, idempotency, retries, side effects, recovery, canonical writes |
| Jev (TypeSafe System One) | Narrow semantic judgments over supplied text/JSON state — Noul, Choice, Score only |
| Local generator | Drafting a note, task title, short answer, or explanation **after** code has selected the operation |
| User | Hosted-processing grants, external disclosure, canonical modifications, new standing grants, improvement activation |

No model is an authority. Jev probability or confidence never grants permission. Confidence is uncertainty information, not authorization and not proof of correctness.

### Replacement invariant

> One durable case runtime and one deterministic decision engine serve every origin. The engine may call bounded judgment or generation providers, but code owns the transition and available action set.

Direct and observed cases use the same runtime, persistence, queue, policy, and operation broker. Origin changes allowed capabilities, urgency, and presentation only.

This **replaces** the former “one local mind / single semantic judge” invariant.

---

## Runtime flow

```text
input adapter
  -> persist source object
  -> append case event
  -> enqueue case
  -> assemble minimal context
  -> generate deterministic candidates
  -> evaluate bounded Jev question set
  -> persist judgment result
  -> apply deterministic decision table
  -> optionally request local text generation
  -> validate generated text against source/capability schema
  -> create feed item, child case, or operation envelope
  -> execute only through policy/approval broker
  -> append outcome evidence
  -> project feed/tasks/memory/diagnostics
```

### Jev unavailable

When Jev is unavailable:

- Continue transcript capture and persistence
- Continue deterministic retrieval and exact glossary lookups
- Continue operations whose semantic decision was already persisted
- Move new semantic work to durable `waiting_for_judgment`
- Retry read-only Jev requests under the bounded retry policy
- Display one non-blocking service status item
- **Never** ask the local generator to make the same semantic judgment as an undeclared fallback

---

## Jev (System One) operational contract

Jev is TypeSafe’s hosted model for fast semantic judgments inside normal software. It is **not** a chatbot, planner, text generator, or autonomous agent.

- Endpoint: `POST https://api.typesafe.ai/v1/systemone`
- Production model: pin `jev-1.13.0` (evaluate upgrades explicitly; do not silently track `jev-latest` in production)
- Text only — RELAY must transcribe/diarize before calling
- Questions over the same state run independently; one answer cannot silently influence another
- Question IDs are response-map keys only; instructions must contain full meaning
- Multi-label detection: one Noul per label (do not force into one Choice)
- “Zero hallucinations” means: cannot invent free-form answers outside the schema. It can still make an incorrect typed decision. See TypeSafe jaggedness docs for known failure modes.

### Primitives

| Primitive | Meaning | Output |
| --- | --- | --- |
| `Noul` | Does a condition hold? | Probability of “yes” 0–1 (no separate confidence) |
| `Choice` | Which member of a defined set fits best? | Winner, option probabilities, confidence |
| `Score` | Where on ordered semantic levels? | Weighted score, level probabilities, confidence |

### Pricing / limits (documented; show to user per operation)

- ~$0.042 per million input tokens; output tokens free
- Limits are documented as dynamic: treat rate limits as subject to change
- Display cost and tokens for each operation

---

## Decision engine

Production dependency:

```csharp
public interface ICaseDecisionEngine
{
    Task<CaseDecision> DecideAsync(
        CaseDecisionRequest request,
        CancellationToken cancellationToken);
}
```

`CaseDecision` is an internal deterministic directive, not model prose. Supported kinds: `wait`, `publish_feed`, `raise_case`, `request_generation`, `request_operation`, `complete`, `no_action`.

Feed text comes from deterministic templates or capability results — never unconstrained Jev prose.

Temporary `CaseMindDecisionAdapter` may exist only while migrating `CaseRuntime`. Delete the adapter and production `ICaseMind` dependency at cutover.

---

## Judgment contracts (summary)

- Typed `JudgmentRequest` / `JudgmentResponse` with Noul / Choice / Score questions and answers
- Persist complete request/response in the content-addressed object store
- Ledger / SQLite / ordinary logs hold only IDs, hashes, question-set id/version, model, status, tokens, latency
- Cache key: SHA-256 over canonical provider + model + question-set + definitions hash + state hash + source hashes
- Persist request before dispatch; persist response before applying the decision
- Failed judgments are never cached as success

Event types: `judgment.requested`, `judgment.completed`, `judgment.failed`, `judgment.deferred`.

Failure categories: `disabled`, `not_authorized`, `missing_secret`, `timeout`, `rate_limited`, `overloaded`, `authentication`, `validation`, `invalid_response`, `network`. Never put transcript state in error strings.

---

## Hosted-processing privacy

Listening and hosted processing are **separate** controls.

Source classifications: `local_only`, `hosted_allowed_session`, `hosted_allowed_project`, `public`. Derived summaries inherit the most restrictive classification of every source used.

A session/project grant covers later Jev calls within purpose, scope, and token budget. Do not generate one approval card per inference. Reject the entire request if any necessary source is excluded — do not silently omit evidence.

---

## Initial question sets

Immutable, versioned assets in `QuestionSetRegistry`. Never assemble instructions from transcript content.

| Set | Role |
| --- | --- |
| `conversation.screen.v1` | Multi-label Nouls + attention Choice over unread segments |
| `direct.route.v1` | Choice: answer / remember / organize / clarify / no_match |
| `acronym.select.v1` | Choice among candidate expansions + no_match (after deterministic lookup) |
| `note.support.v1` | Support / unsupported-claim Nouls after local draft |

Thresholds live in versioned `DecisionThresholds` and are bootstrapping constants, not universal truths. Meeting a threshold never automatically creates a canonical or external side effect.

---

## Local generator boundary

`ITextGenerator` v0.1 methods only: `DraftNoteAsync`, `DraftTaskAsync`, `AnswerAsync`, `DraftClarificationAsync`. Strict JSON schema; validate before use. Must not return moves, capability names, permissions, confidence, new objectives, or tool calls.

If unavailable: verbatim-source drafts for note/task; resumable waiting state for direct answer.

---

## Self-improvement v0.1

Group friction by full `PatternSignature` (kind + capability + project + normalized subject + category) — never by friction kind alone.

Only `memory` (project glossary) and `preference` (filing/presentation) may activate. Evaluation must execute fixtures. Activation: draft → evaluate → approval-bound change set → `active_shadow` → `active` → measurable revert. No improvement may edit the permission broker, disclosure policy, provider configuration, or executable code.

---

## Implementation phases

| Phase | Goal |
| --- | --- |
| 0 | Freeze baseline, characterize, document (this file + STATUS) |
| 1 | Correct product/architecture contract docs |
| 2 | Judgment contracts + fake provider |
| 3 | TypeSafe HTTP gateway |
| 4 | Judgment persistence, cache, recovery |
| 5 | Hosted grants and disclosure gate |
| 6 | Replace model-driven moves with decision engine |
| 7 | Four v0.1 capabilities |
| 8 | Improvement loop (BESS glossary acceptance) |
| 9 | Desktop bind to `CaseRuntimeSurface` |
| 10 | DevHarness + live gates |
| 11 | Delete obsolete production orchestration |

Do not combine phases merely to reduce commit count. Do not remove the legacy runtime until the replacement has passed the stated gates.

Commit message sequence is defined in the implementation plan (one focused commit per phase).

---

## Definition of done (summary)

Alpha remains **not complete** in `STATUS.md` until: one production case runtime + decision engine; no unconstrained model next-action; Jev only via typed question sets; local generation only after code selects the task; independent listening/hosted toggles; durable wait/resume without duplication; four capabilities through desktop; BESS improvement lifecycle; no global leakage; Desktop unbound from legacy orchestration; truthful deterministic vs replay vs live verification; Windows live gates pass.
