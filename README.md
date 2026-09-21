# RELAY

> RELAY notices repeated judgment in your work and offers to turn it into a small, inspectable automation.

RELAY is a local-first Windows assistant for observation, retrieval, and user-approved action across conversations and connected software. It begins as a useful voice and text assistant. As it sees the same kind of need recur, it gathers evidence and proposes a reusable **Reflex**.

RELAY is not an autonomous coding agent, a general-purpose chatbot, or a tool that silently expands its own permissions.

## Product promise

**RELAY turns repeated human judgment into software.**

The user experiences that promise as:

1. RELAY observes an enabled source.
2. RELAY helps with the immediate need.
3. RELAY records the trigger, judgment, user choice, action, and outcome.
4. RELAY notices equivalent work recurring.
5. RELAY proposes a narrow automation and shows the evidence behind it.
6. The user activates, edits, pauses, or rejects that automation.

The activated automation is called a **Reflex**. Reflexes—not chats, prompts, or individual model calls—are the durable unit of product value.

## Alpha goal

The Windows V1 goal is one complete product loop on the TypeScript/Tauri Windows path:

```text
real text or live local speech
    → protected source artifact
    → durable Case
    → deterministic routing / Reflex detection
    → local model when generation is needed
    → bounded Jev judgment when semantic judgment is needed
    → code-owned policy gate
    → useful RELAY result
    → durable outcome / memory / evidence
    → truthful developer trace
    → restart without losing or duplicating work
```

### What Windows V1 currently includes

- Working typed Ask with local model generation when a loopback model server is available.
- Listen intake via allowlisted local audio process (finals only); developer Replay uses a separate ingest path that does not enable Listening.
- Protected DPAPI artifacts and secrets on Windows; feed/source/learning prose is not stored as raw SQLite prose (legacy rows migrate on open).
- One complete Reflex: `resolve-acronym@1` (memory → bundled dictionary → context candidates → optional Jev Choice).
- Developer console as a projection of real Case / Jev / model / policy events.

### What Windows V1 does not claim yet

- Google Workspace, GitHub, or Plaid connectors wired for production use.
- Physical Halo hardware or iPhone/TestFlight adapters.
- Four production Reflexes (only acronym resolution is complete).
- Automatic Reflex generation or self-modifying code.

See `docs/STATUS.md` and `docs/WINDOWS_V1_ACCEPTANCE.md` for the living implementation record.

The alpha does not generate or execute arbitrary code, send email automatically, move money, push Git commits, or grant itself additional access.

## Authority boundaries

| Component | Responsibility |
| --- | --- |
| Deterministic runtime | Persistence, scheduling, source access, search, policy, permissions, budgets, idempotency, retries, execution, and recovery |
| Local model | Conversation, summarization, extraction, drafting, and generation of bounded candidate queries |
| Jev | Fast typed judgments such as source selection, relevance, importance, equivalence, and evidence support |
| Connector | Authentication plus typed observation, read, and write operations for one service |
| User | Connection scopes, hosted-processing grants, write enablement, operation approval, and Reflex activation |

Models advise. Code authorizes and executes.

Exact lookup and known rules remain code. Jev is used only where semantic judgment is required. A confidence score never grants permission.

## Core domain

### Source

A Source is something RELAY may observe or search: a transcript stream, local note collection, Gmail label, Google Calendar, Google Sheet, GitHub repository, public documentation source, or connected financial account.

Raw source content is stored locally as an encrypted object. Ordinary logs and database metadata contain only IDs, hashes, classifications, timestamps, and safe counters.

### Case

A Case is one occurrence of work: a direct question, an observed claim, a possible birthday, a note candidate, or an acronym mention. Cases are durable and independently resumable.

### Judgment

A Judgment is one versioned, schema-bounded Jev question. The complete request and response are persisted before their result affects a Case.

### Operation

An Operation is one typed external or canonical write. It contains an idempotency key, exact connection and action, canonical arguments, source references, requested scope, approval binding, and execution receipt.

### Reflex

A Reflex is a versioned automation containing:

- Trigger and negative triggers
- Conditions
- Permitted sources (`ConnectorRef`)
- Read plan (`ConnectorActionRef`)
- Bounded judgments (`JudgmentDefinitionRef`)
- Permitted write actions (`ConnectorActionRef`)
- Approval mode
- Budgets and retry policy
- Evaluation fixtures
- Explanation template
- Activation defaults
- Explicit rollback policy

Runtime outcome history is stored on `ReflexState`, not on the immutable definition.

Alpha Reflexes are declarative compositions of registered triggers, reads, judgments, and operations. They are never unrestricted generated programs.

## Inputs and assistant behavior

### Text assistant

The composer accepts direct questions and instructions. The local model produces concise answers or summaries from authorized evidence. External facts require a connected source or public-search operation and must include citations.

### Voice assistant

Voice is input through `ITranscriptSource`. The first production adapter accepts Wispr Flow transcription. RELAY does not pretend to perform audio capture or diarization when it only receives text.

Two user states are available:

- **Ask:** one voice or text request directed to RELAY.
- **Listen:** an enabled transcript stream observed quietly for relevant events.

Both states use the same source, Case, Judgment, Operation, and Reflex runtime. Optional local text-to-speech may read direct responses; it is never required for observation.

## Connections and permissions

Every connector is read-only after initial connection. Write access is enabled per action, not through one broad switch.

Each connection exposes:

- `Observe`: ingest new items from selected scopes.
- `Read`: retrieve selected items for an active Case.
- `Propose writes`: allow RELAY to show an approval card for a named action.
- Individual write actions such as `calendar.event.create` or `gmail.draft.create`.

Enabling a write action in the alpha permits proposals only. Every write still requires approval. A later Reflex may receive a narrowly scoped standing grant that is bound to its version, connection, resource scope, and argument constraints.

### Alpha connectors

| Connector | Default observation/read scope | Alpha writes, individually disabled by default |
| --- | --- | --- |
| Local conversation | Enabled only while Ask or Listen is active | Save a local note |
| Local RELAY memory | Read/search | Create or supersede a note; remember an acronym |
| Google Calendar | User-selected calendars | Create/update an event; no delete in alpha |
| Gmail | User-selected labels | Create a draft; no send/delete/archive in alpha |
| Google Sheets | User-selected spreadsheets and ranges | Append/upsert rows; no sheet deletion in alpha |
| GitHub | User-selected repositories | Local draft (no GitHub write); `github.issue-create@1` / `github.issue-comment-create@1` after write reauthorization |
| Public web/Wikipedia | Search and fetch only | None |
| Plaid financial data | User-selected accounts, read-only | None |

ChatGPT connections are not RELAY credentials. RELAY uses its own OAuth/API authorization or a user-installed connector with independently enforced scopes.

## Built-in Reflexes

### 1. Remember birthdays

When enabled, RELAY detects an explicit birthday statement in an authorized transcript or message.

1. The local model extracts person and date candidates.
2. Jev resolves semantic ambiguity only when necessary.
3. Code searches the selected Birthdays calendar.
4. Code detects an existing equivalent annual event.
5. If missing, RELAY proposes a yearly all-day event with a reminder 1,440 minutes before it begins.
6. Approval creates the event using a deterministic idempotency key.

Jev does not decide whether the calendar contains the event; the Calendar API result does.

### 2. Verify technical claims

RELAY extracts a specific, checkable technical claim. Code supplies Jev with a finite list of currently connected and permitted sources. Jev chooses the best first source using a typed Choice. It cannot invent a connector or bypass source policy.

The bounded search sequence is:

1. Select the first eligible source.
2. Generate deterministic and local-model query candidates.
3. Search and retrieve candidate evidence.
4. Use Jev to rank relevance and judge whether the evidence supports, contradicts, or does not resolve the claim.
5. Search another eligible source only when evidence is insufficient, with a fixed source and round budget.
6. Present the result with exact citations.

Eligible sources include personal notes, enabled Gmail labels, selected calendars for date claims, selected GitHub repositories, prior conversation/meeting records, and public documentation or Wikipedia search.

### 3. Preserve important information

RELAY detects information that may be durable and useful: a repeated hyperparameter, product configuration, project fact, decision, constraint, or correction.

1. The local model extracts a note candidate without treating it as truth.
2. Jev judges durable importance and project relevance.
3. Code searches local memory for an equivalent or conflicting note.
4. If an equivalent note exists, RELAY links the new source evidence instead of duplicating it.
5. If no equivalent exists, RELAY proposes or, when explicitly enabled, creates a source-linked local note.
6. Conflicts are shown; they never silently overwrite prior information.

The first canonical note destination is RELAY's encrypted local memory. External note destinations are separate write integrations.

### 4. Resolve acronyms

RELAY detects plausible acronym tokens in enabled sources.

1. Exact project/local glossary lookup runs first without Jev.
2. Local notes, Gmail, GitHub, and previous conversations are searched for candidate expansions.
3. Jev selects among supplied candidates when context is ambiguous.
4. Public search is used only when enabled and local sources fail.
5. RELAY displays the expansion and source without inventing a missing candidate.
6. Repeated accepted resolutions can produce a proposal to remember the mapping.

## Runtime flow

```text
Source adapter
  -> encrypted source object and source event
  -> trigger screening
  -> durable Case
  -> local extraction and deterministic candidates
  -> permission-filtered reads
  -> optional bounded Jev judgments
  -> feed result or Operation proposal
  -> approval and idempotent execution
  -> outcome evidence
  -> pattern aggregation
  -> optional Reflex candidate
```

The UI never pumps the runtime. One application-owned background service processes source synchronization, ready Cases, due retries, approved Operations, and health heartbeats.

## Storage

RELAY uses:

- One authoritative SQLite database in WAL mode for metadata, state, events, queues, grants, connections, Reflexes, and receipts.
- One encrypted content-addressed object store for transcripts, message bodies, documents, prompts, judgment payloads, and other sensitive content.
- Windows-protected credentials for OAuth refresh tokens and provider secrets.
- Versioned database migrations.

There is no second authoritative ledger, parallel session store, or rebuild-only projection database. State changes and their corresponding events are committed in the same database transaction.

## Privacy defaults

- Raw observed content is local-only by default.
- Connector content is not sent to Jev without an applicable session or Reflex disclosure grant.
- Financial content remains local-only unless the user creates a separate explicit disclosure grant.
- Derived content inherits the most restrictive source classification.
- Telemetry is typed and allowlisted; it never includes transcript, email, calendar, financial, prompt, or problem-report prose.
- Disconnecting a source revokes tokens and offers deletion of imported content and derived indexes.

## Versioning

RELAY uses Semantic Versioning for the application and independent integer versions for schemas, connectors, judgments, operations, and Reflexes.

| Version | Goal |
| --- | --- |
| `0.1.0-alpha.1` | One runtime, text assistant, Wispr voice input, Listen mode, encrypted storage, read-only connector framework, local memory |
| `0.1.0-alpha.2` | Google Calendar, Gmail, Sheets, GitHub, public search, and the four built-in Reflexes; write proposals remain approval-bound |
| `0.1.0-alpha.3` | Plaid read-only finance, restart/recovery gates, privacy gates, Windows dogfood telemetry, installer-ready build |
| `0.2.0-beta.1` | PatternEvidence, historical replay, template-bounded Reflex proposals, shadow mode, activation, pause, and rollback |
| `0.2.0-beta.2` | Additional connectors, optional webhook relay, long-running read jobs, and carefully selected standing Reflex grants |
| `1.0.0` | Stable migrations and permissions, recoverable execution, documented connector SDK, and production support guarantees |

Versioned IDs use explicit suffixes:

```text
reflex.remember-birthday@1
reflex.verify-technical-claim@1
reflex.preserve-important-information@1
reflex.resolve-acronym@1
judgment.claim-source-choice@1
operation.google-calendar.event-create@1
connector.google-workspace@1
```

A behavior, schema, permission, or data-classification change requires a new artifact version. Existing active Reflexes stay on their approved version until the user accepts an upgrade.

## Adding a function

### Add a read operation

1. Add a versioned typed operation definition.
2. Declare required connection scopes and returned data classification.
3. Implement cancellation, pagination, rate-limit handling, and a durable cursor when applicable.
4. Normalize results into source objects and safe metadata.
5. Add recorded fixtures and an unavailable-provider test.
6. Add the operation to an explicit connector manifest.
7. Expose it only after the connection and selected resource scope permit it.

### Add a write operation

Complete every read-operation requirement, then also:

1. Default the action to disabled.
2. Define canonical arguments, preconditions, risk class, and deterministic idempotency.
3. Define approval text using deterministic templates.
4. Implement claim, dispatch outside the state lock, receipt persistence, and restart reconciliation.
5. Add duplicate, cancellation, stale-version, denied, timeout, and crash-recovery tests.
6. Add an individual connection-level toggle.

### Add a Reflex

1. Define its trigger and negative triggers.
2. List every permitted source and operation version.
3. Define each bounded judgment and uncertainty path.
4. Set read, hosted-disclosure, write, cost, step, and retry budgets.
5. Add motivating, counterexample, privacy, outage, and restart fixtures.
6. Add a deterministic explanation of why it fired.
7. Start in proposal or shadow mode.
8. Require approval before activation or permission expansion.

No feature may add a generic model-controlled HTTP request, shell command, filesystem mutation, MCP call, or arbitrary tool execution path.

## Repository shape

```text
apps/
  relay/                 Expo Web / shared UI host
  desktop/               Tauri 2 Windows shell (DPAPI, SQLite, audio, secrets)
packages/
  contracts/             shared command/snapshot/event types
  engine/                deterministic runtime (RelayEngine)
  reflexes/              versioned Reflex modules
  ui/                    Feed, Settings, developer console
  storage-schema/        SQLite migrations
  testkit/               recorded fixtures and harness helpers
adapters/
  node/                  Node SQLite / file adapters (tests + smoke)
  tauri/                 Tauri invoke ports
  expo/                  Expo/browser demo adapters
tools/
  verification/          shared V1 gate manifest
  e2e/                   headed Windows journeys
  manual/                Node preflights (not desktop E2E)
  replay/                replay CLI + architecture boundary tests
  halo/                  Halo display protocol emulator
  audio/                 local ASR packaging
dev/                     Tauri launch, model start, readiness scripts
docs/                    status, acceptance, architecture, privacy, Reflexes
```

The retired C# / WinUI tree is preserved only by tag `relay-dotnet-a6bf987`
(`docs/archive/DOTNET_BASELINE.md`). It is not a competing implementation target.

The production solution does not compile legacy minds, generated-tool workers, autonomous agents, or generic workflow synthesis. Useful prototypes remain available in Git history and may return only through a versioned beta design.

## Alpha release gates

The alpha is not complete until a clean Windows installation proves:

1. Text and Wispr voice requests use the same durable runtime.
2. Listening continues while direct questions and approvals are pending.
3. Enabled sources synchronize without raw content entering logs.
4. Every connector begins read-only.
5. Every write action is separately enabled and still approval-bound.
6. Each built-in Reflex works end to end with exact evidence.
7. Jev outage pauses only judgment-dependent work.
8. Restart recovers source cursors, Cases, Judgments, and Operations without duplicate effects.
9. Cancellation never dispatches a new effect.
10. The privacy sentinel appears only in its authorized encrypted object.
11. The app records enough typed dogfood telemetry to diagnose stalls without exposing user content.
12. Legacy production orchestration is absent from the shipped binaries.

