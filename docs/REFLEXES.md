# Reflexes

A Reflex is a versioned declarative automation: trigger, conditions, permitted sources, read plan, bounded judgments, action template, approval mode, fixtures, activation record, and outcome history. It is not generated code.

`ReflexResult` may publish a finding, record evidence, request a bounded read, propose an operation, ask a clarification, or finish with no action. It cannot execute a write.

Production registers only these four definitions in the alpha:

## `reflex.remember-birthday@1`

Detect a birthday statement, extract person and date locally, use Jev only for semantic ambiguity, search the selected Birthdays calendar in code, and propose an annual all-day event with a reminder 1,440 minutes before it starts. Idempotency key: `birthday:{connection}:{calendar}:{person-key}:{MM-dd}`. Jev does not decide whether the event exists.

## `reflex.verify-technical-claim@1`

Extract a checkable claim, enumerate permission-filtered sources, and let Jev choose among that finite list. Search, retrieve, then judge support. At most three sources and two post-retrieval judgment rounds. Terminal results are `supported`, `contradicted`, or `insufficient`, each with exact citations or an honest insufficient. No write.

## `reflex.preserve-important-information@1`

Extract a possible durable fact, decision, constraint, configuration, or correction. Jev judges importance and relevance. Code searches local memory, links equivalent notes, and shows conflicts instead of overwriting them. A new note is proposed unless the local-note Reflex toggle is explicitly enabled.

## `reflex.resolve-acronym@1`

Search exact project glossary, then local notes, previous conversations, Gmail and GitHub, then public search. Jev selects among supplied candidates. It cannot invent an expansion that is not in the evidence. Remembering an accepted expansion is a separate local-memory operation.

Active Reflexes stay on their approved version until the user explicitly upgrades them.
