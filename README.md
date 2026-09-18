# Relay

Relay is a **local-first desktop conversation-to-action learner**: it listens to approved transcript streams, identifies useful events with bounded hosted judgments (Jev), turns them into source-linked notes/tasks/lookups, and improves a small set of project-specific preferences through measured, reversible changes.

It does **not** begin as an autonomous agent that writes new code or decides its own permissions.

**RELAY** is the whole system: a durable case runtime, a deterministic decision engine, optional Jev judgments under explicit hosted-processing grants, a local text generator for drafting only, personal memory, and an approval-bound operation broker. Initial personalization lives in memory, preferences, and glossary entries. Those assets must survive replacing any model. Training model weights from collected examples is a later possibility, not part of the essential build.

This README is the essential target. It is not a status report.

**Authoritative docs:** [`docs/PRODUCT.md`](docs/PRODUCT.md) · [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) · [`docs/JEV_REFACTOR.md`](docs/JEV_REFACTOR.md) · [`docs/STATUS.md`](docs/STATUS.md). Historical documents live under [`docs/archive/`](docs/archive/). Local testing ground: [`dev/`](dev/).

Windows 11 is the first surface. The architecture is not Windows-specific.

## Observe, judge, decide, act, resume

The runtime collects input and persists source objects before processing. Conversation is one origin among equals: the runtime buffers timestamped transcript segments and submits windows on a configured cadence. Window length and evaluation frequency are separate settings. Original passages remain retrievable beyond the current prompt.

**Deterministic code** owns candidates, policy, permissions, and the next transition. **Jev** (TypeSafe System One) supplies narrow typed judgments — Noul, Choice, Score — over supplied text/JSON state when a hosted grant allows it. A **local generator** drafts note/task/answer text only after code has selected that generation task. Jev probability or confidence never grants permission.

A window can produce a note, a task proposal, an acronym resolution, a clarification, or no action. Direct requests and observed conversation share one case runtime, queue, policy, and operation broker. Origin affects authorization, urgency, presentation, and allowed capabilities only.

```
observation ──► persist source ──► assemble context ──► optional Jev judgments
                                                         │
                                                         ▼
                                              deterministic decision engine
                                                         │
                 ┌───────────────────────────────────────┼───────────────────────────┐
                 ▼                                       ▼                           ▼
        local draft (schema)                   feed / child case              operation envelope
                 │                                       │                           │
                 └──────────── policy · approvals · budgets · recovery · projections ┘
```

## Asynchronous work

**Cases wait independently. Direct work is not blocked by a waiting observed case or a pending judgment.**

When Jev is unavailable, RELAY continues capture and deterministic lookups, moves new semantic work to durable `waiting_for_judgment`, and never asks the local generator to make the same judgment as an undeclared fallback.

The runtime serializes changes to each case, records processed events, and checks versions before resuming. Late results cannot silently revive cancelled work or execute an obsolete plan.

## v0.1 capabilities

| Capability | Purpose |
| --- | --- |
| Note capture | Source-linked notes from observed or direct input; canonical writes need approval |
| Task capture | Local task proposals; owner/date only when explicitly stated |
| Acronym resolve | Exact glossary lookup first; Jev only when ambiguous; no invented expansions |
| Direct answer | Local generator over approved local evidence with citations; no hidden research |

One improvement family: project-scoped glossary or preference changes from repeated equivalent corrections (or one explicit instruction), evaluated, approved, shadowed, measured, and reversible.

Deferred until later releases: research/delegation, generated tools, workflow synthesis, third-party connectors, and automatic permission expansion.

## How Relay becomes personal

Task outcomes, repeated instructions, and user corrections provide friction evidence. RELAY groups that evidence by a stable pattern signature (not by friction kind alone), proposes a scoped change, evaluates fixtures, and activates only after approval.

1. **Identify the friction** with concrete examples.
2. **Specify the change** (glossary entry or preference) with before/after and success criteria.
3. **Evaluate** motivating cases and counterexamples (including project isolation).
4. **Approve and activate** through an approval-bound change set; start in shadow, then promote.
5. **Measure and revert** when predeclared failure conditions are met.

Improvements remain versioned, inspectable, and removable. They must not edit the permission broker, disclosure policy, provider configuration, or executable code.

## Guiding heuristics

1. **Treat Relay as a conversation-to-action learner first.** Useful notes, tasks, and lookups come before tools and workflows.
2. **Treat models as advisors, not authorities.** Code owns transitions; Jev judges narrowly; the local model drafts text.
3. **Treat personalization as accumulated, reversible configuration.** Memory and preferences belong to the user and survive model replacement.
4. **Treat improvement as a measured hypothesis.** Track corrections, unresolved results, and unnecessary Jev calls; proposal count is not the goal.
5. **Spend attention deliberately.** Preserve useful context quietly. Prioritize direct requests and time-sensitive findings.
6. **Use uncertainty to select the next step.** Missing evidence can call for clarification or wait — never for inventing facts or silent research.
7. **Preserve intent while adapting the approach.** Material changes to objective, deliverable, or scope require approval.

## What the essential build must prove

These scenarios run through the desktop with real providers where required.

- An observed window produces a useful, source-linked note or task; corrections update the same work.
- Ambiguous acronyms resolve via Jev when granted; exact project glossary hits need zero Jev calls.
- A direct question completes while listening and another approval remain active.
- Jev outage: capture continues; semantic work waits and resumes without duplication.
- Three project-scoped `BESS` corrections produce one glossary improvement through evaluation → approval → shadow → activation → revert, without leaking to other projects.

The first production build needs: durable case runtime, deterministic decision engine, pinned Jev client with cost/token display, local generator for drafting, persistent source storage, independent listening and hosted-judgment toggles, and one feed and composer.

Status and verification levels: [`docs/STATUS.md`](docs/STATUS.md).
