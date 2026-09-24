# Reflexes

A Reflex is a versioned declarative automation: trigger, negative triggers, conditions, permitted sources, read plan, bounded judgments, permitted write actions, approval mode, budgets, retry policy, fixtures, explanation template, activation defaults, and an explicit rollback policy. It is not generated code. Those fields are first-class on `ReflexDefinition`; handlers must not hide policy.

Runtime outcome history lives on `ReflexState` / `ReflexRunSummary`, not on the immutable definition.

`ReflexContext` carries trigger source references, case version (`long`), eligible connections, remaining budgets, and current time.

`ReflexResult` is a closed hierarchy (`FindingResult`, `ReadRequestedResult`, `OperationProposedResult`, `ClarificationRequiredResult`, `NoActionResult`). Invalid Kind/field combinations are unrepresentable.

An `OperationProposal` supplies action reference, arguments, input source refs, requested resource scope, and typed preconditions. The operation broker canonicalizes arguments, resolves granted scope from policy, and calculates hash / idempotency key.

Identifier display uses exact versions, for example `reflex.remember-birthday@1` and `google-calendar@1/google-calendar.event-create@1`.

The designs below are not the registered runtime set. Production code registers four modules: `reflex.resolve-acronym`, `reflex.capture-note`, `reflex.remember-fact`, and `reflex.recommend-next-action`. None is `shipped`. Pass 1 adds a shared event runner for connected events and local Case actions. It does not make the calendar write designs below a live provider.

Design notes kept for the later catalog:

## `reflex.remember-birthday@1`

Detect a birthday statement, extract person and date locally, use Jev only for semantic ambiguity, search the selected Birthdays calendar in code, and propose an annual all-day event with a reminder 1,440 minutes before it starts. Idempotency key: `birthday:{connection}:{calendar}:{person-key}:{MM-dd}`. Jev does not decide whether the event exists.

## `reflex.verify-technical-claim@1`

Extract a checkable claim, enumerate permission-filtered sources, and let Jev choose among that finite list. Search conversation, Gmail, Calendar, GitHub (issues/PRs/code/content/comments), and public sources, then judge support. At most three sources and two post-retrieval judgment rounds. Terminal results are `supported`, `contradicted`, or `insufficient`, each with exact citations or an honest insufficient. No write.

## `reflex.preserve-important-information@1`

Triggers may come from any enabled observed source. Extract a possible durable fact, decision, constraint, configuration, or correction. Jev judges importance and relevance. Code searches local memory, links equivalent notes, and shows conflicts instead of overwriting them. A new note is proposed unless the local-note Reflex toggle is explicitly enabled.

## `reflex.resolve-acronym@1`

Search exact project glossary, then local notes, prior conversation, Gmail and GitHub search, then public search. Jev selects among supplied candidates. It cannot invent an expansion that is not in the evidence. Remembering an accepted expansion is a separate local-memory operation.

Active Reflexes stay on their approved version until the user explicitly upgrades them.
