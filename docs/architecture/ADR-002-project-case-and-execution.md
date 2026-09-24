# ADR-002: ProjectCase, Execution, observation, and Verify

Status: accepted for Pass 1. This record distinguishes the target from behavior that has proof.

## Decision

A **ProjectCase** is one durable space for a project or line of work. It has a stable id, a protected folder, and `main.md` beginning with `## Case Intent`. It holds accepted context, rules, references, and entries. It does not copy an external repository.

An **ExecutionRecord** is one phased job for one input (intake, detect, judge, execute, and the rest of the existing phase list). The legacy `cases` table and `caseId` on old traces mean this job. They do not mean a ProjectCase. `RunManifestV1` remains a diagnostic process run. `reflex_runs` remains reflex history. The developer console label “Run Events” stays on that diagnostic stream.

An Execution may link to zero, one, or many ProjectCases through `execution_case_links`. Nothing forces a match when an input arrives.

**Microphone Listening** and **external observation** are separate controls. Listening gates microphone retention. A selected resource binding gates connected-source retention. Replay of a prepared file does not require Listening.

**Verify** is the review inbox. Evidence status (`Verified`, `Contradicted`, `Needs evidence`, `Proposal`) is not a disposition (`pending`, `accepted`, `dismissed`, `superseded`). A verified item is not silently written into a Case. Direct user-authorized edits may commit with Undo. Inferred changes wait in Verify.

## Pass 1 proof

Pass 1 uses a deterministic calendar event adapter behind the same `EventEnvelope` intake the real provider must use in Pass 2. That adapter is test-only and must not ship in a release composition. A scoped action in Pass 1 is a reversible local Case edit with a receipt. It is not a provider write.

## Pass 2 boundary

Real external connectors, on-device mobile model and speech, hosted external-AI delegation, and physical Halo stay `not-shipped` until Pass 2 has exact-build evidence. Hosted Jev remains a separate disclosure grant and budget. A stronger external model needs a per-task approval for destination, context, and cost limit.

## Consequences

Production code must not treat local authority state as “connected”, “executed”, or “shipped”. Provider success requires a provider receipt. G1 is not green because a private canary file exists or because one workflow paragraph records HTTP 200 while the same file and the capability matrix still say the live canary has not run.
