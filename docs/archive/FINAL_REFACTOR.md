# RELAY final alpha refactor

This document replaces the additive migration strategy. The current repository contains a useful durable-runtime prototype and a large legacy orchestrator. The alpha should ship one architecture only.

Baseline reviewed: `94465cf5d1bdbfc678c9261c71542bf2e14ee903`.

## Final product boundary

RELAY alpha is a local-first Windows observation assistant with:

- Text Ask mode.
- Voice Ask and Listen modes through `ITranscriptSource` and Wispr Flow.
- Local conversation and summarization through the configured local model.
- Bounded Jev judgments.
- Read-only-by-default connectors.
- Individually enabled, approval-bound connector writes.
- Four built-in declarative Reflexes.
- Evidence collection for template-bounded personal Reflexes in beta.

Anything that does not support this boundary is deleted from the production branch or moved to an uncompiled experimental archive.

## Architecture decisions

1. SQLite is the single authoritative metadata/state store.
2. Sensitive bodies use an encrypted content-addressed object store.
3. One `RelayRuntimeService` owns background processing.
4. One `RelayApplication` command/query facade is presented to WinUI.
5. Connectors provide typed observations, reads, and writes.
6. All writes pass through `OperationEnvelope` and `OperationExecutor`.
7. Built-in and learned behaviors share the same `ReflexDefinition` contract.
8. The local model and Jev are never execution authorities.
9. The alpha contains no arbitrary generated code or generic workflow engine.
10. Read permission, observation, hosted disclosure, write enablement, and Reflex standing grants remain separate.

## Target solution

Keep four production projects and three test projects:

```text
src/Relay.Core
src/Relay.Infrastructure
src/Relay.Connectors
src/Relay.Desktop
tests/Relay.Core.Tests
tests/Relay.Integration.Tests
tests/Relay.Desktop.Tests
```

`Relay.DevHarness` may remain as an unshipped executable until its useful scenarios have become integration tests. Remove `Relay.Worker` from the production solution.

### Relay.Core

No Win32, HTTP, provider SDK, filesystem-layout, or UI dependencies.

```text
Application/
  RelayApplication.cs
  Commands.cs
  Queries.cs
  RelayRuntimeService.cs
Cases/
  Case.cs
  CaseEvent.cs
  CasePhase.cs
  CaseRepository.cs
Sources/
  SourceDefinition.cs
  SourceEvent.cs
  SourceSliceRef.cs
  ITranscriptSource.cs
Connectors/
  ConnectorDefinition.cs
  Connection.cs
  ConnectorOperationDefinition.cs
  IConnector.cs
Judgments/
  JudgmentDefinition.cs
  JudgmentRequest.cs
  JudgmentResult.cs
  IJudgmentService.cs
Operations/
  OperationEnvelope.cs
  OperationPolicy.cs
  OperationReceipt.cs
  IOperationExecutor.cs
Reflexes/
  ReflexDefinition.cs
  ReflexEvidence.cs
  ReflexCandidate.cs
  ReflexEvaluator.cs
Memory/
  Note.cs
  AcronymEntry.cs
  IMemorySearch.cs
Security/
  DataClassification.cs   # DisclosureClass, DataSensitivity, DataPolicy
  ArtifactRefs.cs         # ConnectorRef, ConnectorActionRef, JudgmentDefinitionRef, ReflexRef
  DisclosureGrant.cs
  ConnectorGrant.cs
Telemetry/
  ProductEvent.cs
```

### Relay.Infrastructure

```text
Persistence/
  RelayDatabase.cs
  Migrations/
  SqliteCaseRepository.cs
  SqliteOperationRepository.cs
  SqliteReflexRepository.cs
  SqliteConnectionRepository.cs
  SqliteSourceRepository.cs
Objects/
  EncryptedObjectStore.cs
  ObjectMetadata.cs
Judgments/
  JudgmentLifecycle.cs
  JudgmentCache.cs
  TypeSafeJudgmentClient.cs
Models/
  OpenAiCompatibleLocalModel.cs
Security/
  WindowsCredentialVault.cs
  DisclosurePolicy.cs
Telemetry/
  JsonlDiagnosticSink.cs
```

### Relay.Connectors

```text
Local/
  ConversationConnector.cs
  MemoryConnector.cs
Google/
  GoogleOAuth.cs
  GmailConnector.cs
  GoogleCalendarConnector.cs
  GoogleSheetsConnector.cs
GitHub/
  GitHubConnector.cs
PublicSearch/
  WebSearchConnector.cs
  WikipediaConnector.cs
Finance/
  PlaidConnector.cs
```

### Relay.Desktop

```text
App.xaml.cs
MainWindow.xaml
MainWindow.xaml.cs
Runtime/RelayDesktopHost.cs
Transcript/WisprFlowTranscriptSource.cs
ViewModels/MainViewModel.cs
ViewModels/ConnectionsViewModel.cs
ViewModels/ReflexesViewModel.cs
Views/ConnectionsView.xaml
Views/ReflexesView.xaml
Windows/HotkeyService.cs
Windows/SingleInstanceService.cs
```

## Canonical data model

Use a migration-managed SQLite database with foreign keys and WAL enabled.

Required tables:

```text
schema_migrations
objects
sources
source_events
connections
connection_scopes
connector_cursors
cases
case_events
case_waits
judgments
operations
operation_receipts
notes
note_sources
acronyms
reflex_definitions
reflex_evidence
reflex_candidates
reflex_runs
grants
product_events
```

State changes append a domain event and update current state in one database transaction. JSONL is diagnostic output only and is never authoritative.

Object payloads are encrypted with AES-GCM. The encryption key is protected with Windows DPAPI. Object metadata contains classification and SHA-256 of the plaintext for integrity and deduplication. Logs never contain object payloads.

## Runtime contracts

### Transcript source

```csharp
public interface ITranscriptSource : IAsyncDisposable
{
    string SourceId { get; }
    IAsyncEnumerable<TranscriptSegment> ReadAsync(CancellationToken cancellationToken);
}
```

A segment contains stable ID, timestamp, optional speaker, text, final/interim state, and source-specific cursor. Only final segments enter the durable source pipeline. Interim text is UI-only.

### Connector

```csharp
public interface IConnector
{
    ConnectorDefinition Definition { get; }
    Task<ConnectionHealth> CheckAsync(Connection connection, CancellationToken ct);
    Task<ObservationPage> ObserveAsync(Connection connection, ConnectorCursor? cursor, CancellationToken ct);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct);
    Task<OperationReceipt> ExecuteAsync(ConnectorWriteRequest request, CancellationToken ct);
    Task<OperationReconciliation> ReconcileAsync(ConnectorReconcileRequest request, CancellationToken ct);
}
```

Connectors return normalized `ObservedItemDraft` items and opaque provider cursors. Write/reconcile requests include connection, arguments, scopes, hash, attempt ID, and source refs. Connector code does not decide which Reflex runs and does not call Jev directly.

### Reflex handler

```csharp
public interface IReflexHandler
{
    ReflexDefinition Definition { get; }
    Task<ReflexResult> EvaluateAsync(ReflexContext context, CancellationToken ct);
}
```

`ReflexResult` may publish a finding, record evidence, request another bounded read, propose an Operation, ask a clarification, or finish with no action. It cannot execute a write.

## Permission model

For every connection store independent state for:

```text
connected
observationEnabled
selectedResources
readScopes
writeActionEnabled[actionId]
hostedDisclosureEnabled[purpose]
```

Alpha write states are:

```text
disabled
approval_required
```

Beta adds `reflex_grant`, bound to exact Reflex version, connection ID, resource scope, operation version, argument constraints, expiry, and budget.

Never interpret a connector-level write toggle as automatic execution permission.

## Source planning with Jev

Jev may choose among a finite candidate list already filtered by connection status, read scope, classification, and Reflex definition.

For technical claims:

1. Local extraction creates a structured claim and up to three search-query candidates.
2. Code enumerates eligible sources.
3. `judgment.claim-source-choice@1` uses Choice with one criterion per eligible source plus `no_match`.
4. Code searches the chosen source.
5. Code retrieves candidate passages.
6. `judgment.evidence-relevance@1` scores or selects relevant passages.
7. `judgment.claim-support@1` returns `supported`, `contradicted`, or `insufficient` with calibrated probability.
8. If insufficient, code repeats with remaining sources up to three source attempts.
9. The final result cites exact source slices.

Jev never receives arbitrary connector names, credentials, generic tool execution, or permission to create a query side effect. This follows TypeSafe's intended route/select/verify composition: known rules and execution remain code while typed judgments supply semantic selection.

## Built-in Reflex implementation

### `reflex.remember-birthday@1`

Triggers:

- Final transcript/message segment containing a likely birthday statement.
- Direct request to remember a birthday.

Reads:

- Local person memory.
- Selected Google Birthdays calendar.

Judgments:

- `birthday-statement@1`: does the source assert a birthday?
- `person-candidate-choice@1`: select among supplied people when ambiguous.
- `date-meaning-choice@1`: select among supplied date interpretations when ambiguous.

Deterministic checks:

- Valid month/day.
- Exact and normalized duplicate calendar lookup.
- Conflict detection.
- Idempotency key `birthday:{connection}:{calendar}:{person-key}:{MM-dd}`.

Write:

- `google-calendar.event-create@1`.
- Annual recurrence.
- All-day event.
- Reminder override of 1,440 minutes.
- Approval required in alpha.

### `reflex.verify-technical-claim@1`

Triggers:

- Specific declarative technical claim from a final transcript, direct question, Gmail item, or GitHub discussion.

Reads:

- Local notes.
- Prior transcript/meeting objects.
- Enabled Gmail labels.
- Selected Google Calendar only for date/schedule claims.
- Selected GitHub repositories.
- Public documentation/Wikipedia search.

Judgments:

- Claim checkability.
- Source choice.
- Evidence relevance.
- Evidence support/contradiction.
- Materiality of interruption.

Limits:

- At most three sources.
- At most two judgment rounds after retrieval.
- No finding without citations.
- `insufficient` is a valid terminal result.

Write:

- None. It may create a local finding and Reflex evidence.

### `reflex.preserve-important-information@1`

Triggers:

- Final source item containing a possible durable decision, fact, constraint, correction, configuration, or repeated parameter.

Reads:

- Local notes and acronym memory.
- Current project/source context.

Judgments:

- Durable importance.
- Project relevance.
- Equivalence or conflict among supplied candidate notes.

Deterministic checks:

- Exact normalized duplicate.
- Existing note linkage.
- Source-slice integrity.

Write:

- Create or supersede a local note.
- Automatic local note writing remains a separate Reflex toggle; otherwise approval is required.

### `reflex.resolve-acronym@1`

Triggers:

- Plausible uppercase or domain-specific token.

Reads:

- Local glossary first.
- Local notes and prior transcripts.
- Gmail and GitHub when enabled.
- Public search last.

Judgments:

- Select among supplied expansion candidates.
- Decide whether public search is warranted when local candidates are absent.

Deterministic checks:

- Token allow/deny list.
- Exact project/global glossary lookup.
- Candidate deduplication.

Write:

- None for lookup.
- Remembering an accepted expansion is a separate local-memory Operation.

## Current-code disposition

### Keep and harden for alpha

| Current code | Disposition |
| --- | --- |
| `Cases/ObjectStore.cs` | Keep the content-addressed concept; replace JSON sidecars with typed DB metadata and add encryption |
| `Cases/CaseModels.cs` | Keep ID, origin, version, budgets, parentage; replace stringly typed status/waits/source refs |
| `Cases/CaseStore.cs` | Replace file records/events with transactional SQLite repositories |
| `Cases/OperationEnvelope.cs` | Keep canonical hashing and idempotency concepts; add connection/action/risk/recovery fields |
| `Cases/OperationStore.cs` | Replace file store with DB repository and unique idempotency constraint |
| `Cases/ReadyQueue.cs` | Merge into a durable `work_items`/due-time database queue |
| `Cases/StreamIntake.cs` | Keep persist-before-processing; generalize under `ITranscriptSource` and `SourceEvent` |
| `Composition/RelayComposition.cs` | Keep one composition root; rebuild around the four final projects |
| `Judgments/*` | Keep typed contracts, store, cache, and lifecycle; fix source references, recovery, and budget accounting |
| `Privacy/*` | Keep classifications and explicit grants; make every query session/Reflex scoped |
| `Capabilities/AcronymCandidateBuilder.cs` | Reuse token/candidate logic inside acronym Reflex |
| `Capabilities/AcronymResolveCapability.cs` | Reuse deterministic-first behavior; replace empty judgment source refs |
| `Capabilities/NoteCaptureCapability.cs` | Reuse support-verification intent; remove raw prose from case events |
| `Generation/*` | Keep local generation interface and schema validation |
| `Gateway/TypeSafeJudgmentClient.cs` | Keep after current API-contract verification |
| `Gateway/OpenAiCompatibleClient.cs` | Keep as local-model gateway |
| `Windows/DpapiSecretStore.cs` | Keep; extend to protect an object-encryption master key and OAuth refresh tokens |
| `Windows/HotkeyListener.cs` and `SingleInstance.cs` | Keep and test both global hotkeys |
| `Telemetry/*` | Keep pipeline intent; replace arbitrary property bags with typed allowlisted events |

### Reuse as beta design input, not alpha production code

| Current code | Beta opportunity |
| --- | --- |
| `Usage/PatternSignature.cs` | Starting point for normalized `ReflexPatternKey` |
| `Usage/FrictionEvidenceStore.cs` | Starting point for `ReflexEvidenceRepository`; remove raw detail and hard-coded seven-day grouping |
| `Usage/ImprovementProposal.cs` | Starting point for `ReflexCandidate`; preserve examples, permissions, evaluation, reversion, and success metrics |
| `Usage/ImprovementEvaluator.cs` | Preserve lifecycle idea; replace synthetic glossary-specific uses with historical case replay |
| `Evaluation/*` | Adapt fixture runner to Reflex simulation against immutable source snapshots |
| `SelfChange/ChangeSets.cs` | Reuse activation/rollback concepts for declarative Reflex versions |
| `Workflows/WorkflowDefinition.cs` | Mine validation and bounded-step ideas; do not ship the generic workflow runtime |
| `Tools/ToolPackage.cs` and `ToolStore.cs` | Potential future signed executable Reflex package format |
| `Agents/*`, `Relay.Worker`, `JobObjectWorkerHost` | Potential beta isolation for long-running or generated code after the declarative Reflex system is stable |
| `Attention/AttentionArbiter.cs` | Reuse later for interruption ranking across many active Reflexes |
| `Backup/BackupService.cs` | Adapt to SQLite online backup plus encrypted objects |
| `Projects/ProjectRegistry.cs` | Mine for optional source grouping; do not require project folders in the general alpha |
| `Search/SearchIndex.cs` | Reuse normalization/token tests; replace in-memory index with SQLite FTS5 |

### Delete from the alpha branch after replacement tests pass

```text
src/Relay.Core/Session/**
src/Relay.Core/Mind/**
src/Relay.Core/Orchestration/**
src/Relay.Core/State/**
src/Relay.Core/Tasks/**
src/Relay.Core/External/**
src/Relay.Core/Execution/**
src/Relay.Core/Policy/** once OperationPolicy replaces it
src/Relay.Core/Stream/** once SourceEvent replaces it
src/Relay.Core/Ledger/** once SQLite domain events are authoritative
src/Relay.Core/Captures/** once SourceEvent replaces it
src/Relay.Desktop/MainWindow.Attention.cs
src/Relay.Desktop/MainWindow.Feed.cs
src/Relay.Desktop/MainWindow.Panels.cs
src/Relay.Worker/**
```

Remove `Relay.Worker` and its copy target from `Relay.Desktop.csproj`. Remove legacy test projects from the production solution after extracting the characterization tests that still describe required behavior. Git history is the archive; dead production code does not need to remain compiled.

## UI replacement

Replace the current drawer-heavy window with five surfaces:

1. **Assistant** — chronological feed, Listen toggle, hosted-judgment status, composer, microphone/hotkey state.
2. **Activity** — safe event summaries and evidence links.
3. **Suggestions** — pending operations and Reflex candidates.
4. **Reflexes** — built-in and learned Reflex versions, status, scope, outcomes, pause/revert.
5. **Connections** — connection health, selected resources, Observe toggle, and individual write-action toggles.

Remove Projects, Review, Tasks, Inbox, Ledger, Preferences, and Diagnostics drawers from the main interaction surface. Required diagnostics belong in a separate developer panel enabled only in dogfood builds.

The UI submits commands and reads immutable snapshots/view models. It never calls `StepNext`, owns queues, or starts fire-and-forget runtime work.

## Refactor execution order

### Commit 1: freeze and characterize

1. Tag `94465cf` as the final additive prototype.
2. Replace the root README with `README.next.md`.
3. Move the current `PRODUCT.md`, `ARCHITECTURE.md`, and `JEV_REFACTOR.md` into the historical archive because they explicitly defer connectors and describe the superseded additive architecture.
4. Add concise authoritative `CONNECTORS.md`, `REFLEXES.md`, `PRIVACY.md`, and `STATUS.md` documents derived from the new contract. Do not retain two competing product specifications.
5. Add tests for all behaviors being retained.
6. Add privacy sentinel and crash-point tests.
7. Stop adding code to legacy namespaces.

Acceptance: the baseline test manifest records exact commands, commit, environment, and results.

### Commit 2: create the new solution boundary

1. Add `Relay.Infrastructure` and `Relay.Connectors`.
2. Define Core interfaces without provider dependencies.
3. Remove the Worker reference from Desktop.
4. Create an explicit dependency test that rejects Core references to Desktop, Windows, HTTP clients, or connector implementations.

Acceptance: all production dependency edges point inward to Core.

### Commit 3: consolidate persistence

1. Add schema migrations and authoritative SQLite repositories.
2. Add encrypted object storage.
3. Add a one-time importer for prototype cases/objects only if dogfood data must be preserved.
4. Eliminate new writes to legacy ledger, projection, session, and staging stores.

Acceptance: killing the process after any committed transition yields one reconstructible state with no cross-store repair.

### Commit 4: replace the runtime loop

1. Add `RelayRuntimeService` with one owned task and cancellation token.
2. Add source-sync, ready-case, due-wait, operation, and heartbeat work kinds.
3. Execute provider calls outside database transactions.
4. Implement durable claim and reconciliation.
5. Remove UI pumping.

Acceptance: the headless runtime progresses and recovers without any window or renderer.

### Commit 5: finish text and voice assistant

1. Implement `TextInputSource` and `WisprFlowTranscriptSource`.
2. Separate interim UI transcript from final durable segments.
3. Route Ask and Listen through the same source pipeline.
4. Fix both global hotkeys.
5. Add optional local TTS behind a setting.

Acceptance: voice and text requests produce equivalent Cases and direct answers while observation remains active.

### Commit 6: connector framework and read-only sources

1. Add connection records, selected resources, scopes, cursors, and health.
2. Add Google OAuth and Calendar/Gmail/Sheets read paths.
3. Add GitHub selected-repository read paths.
4. Add public web/Wikipedia search.
5. Add Plaid sandbox read synchronization.
6. Index normalized source items in SQLite FTS5.

Acceptance: every connector can be connected, scoped, synchronized, disconnected, and data-deleted without any write permission.

### Commit 7: typed write proposals

1. Add per-action write settings.
2. Implement Calendar event create/update.
3. Implement Gmail draft create.
4. Implement Sheets append/upsert.
5. Implement GitHub issue/comment draft and approval-bound publish separately.
6. Leave finance write-free.

Acceptance: disabled actions cannot be proposed or executed; enabled actions still require approval; duplicate dispatch creates one external effect.

### Commit 8: four built-in Reflexes

Implement each Reflex independently with recorded provider fixtures and end-to-end tests. Register only the four versioned definitions in production.

Acceptance: every Reflex handles success, no match, ambiguity, provider outage, Jev outage, revoked scope, restart, cancellation, and duplicate delivery.

### Commit 9: evidence and beta seed

1. Record `ReflexEvidence` after real user outcomes.
2. Add template-bounded grouping by full normalized signature.
3. Display evidence counts without activating anything automatically.
4. Add a candidate preview that replays the built-in template on historical cases.

Acceptance: RELAY can explain why it believes a missing automation exists without fabricating three synthetic examples.

### Commit 10: delete the parallel product

1. Replace `MainWindow` completely.
2. Delete legacy namespaces listed above.
3. Remove legacy settings and data-root paths.
4. Delete or archive tests for removed behavior.
5. Verify shipped assemblies do not reference the removed namespaces or Worker.

Acceptance: there is one runtime, one UI path, one storage authority, one permission broker, and one operation executor.

### Commit 11: production gates

1. Run the complete unit and integration suites.
2. Run Google, GitHub, and Plaid sandbox contract tests.
3. Run a fresh-root Windows dogfood session.
4. Run crash injection at source persistence, judgment persistence, operation claim, external dispatch, and receipt persistence.
5. Run privacy sentinel scans.
6. Build an unpackaged dogfood executable and a packaged alpha installer.
7. Publish a sanitized verification manifest tied to the exact commit.

## Non-negotiable tests

### Runtime

- Direct Ask completes while Listen and unrelated waits remain active.
- Due retries do not busy-loop.
- Provider calls never execute under a global state lock.
- Restart reuses completed Judgments and Operations.
- A cancelled Operation is never newly dispatched.

### Connectors

- Initial connection has zero write actions enabled.
- Removing one selected resource makes it immediately unavailable to search.
- OAuth revocation changes connection health and stops synchronization.
- Pagination and duplicate webhook/poll delivery are idempotent.
- Disconnect plus delete removes imported objects and derived indexes.

### Privacy

- A unique transcript/email/financial sentinel occurs only in its encrypted object.
- Local-only sources cannot enter a Jev request.
- Telemetry contains no arbitrary user strings.
- A derived result inherits the strictest source classification.

### Reflexes

- Birthday duplicate and conflict handling.
- Technical claim `supported`, `contradicted`, and `insufficient` paths with citations.
- Important-note duplicate, conflict, and source-link behavior.
- Acronym exact, ambiguous, absent, and public-search paths.
- Each Reflex explains its trigger and selected sources.

### Operations

- Canonical argument edit invalidates approval.
- Disabled write action cannot execute even with a stale approval.
- Idempotency survives process termination.
- Reconciliation handles an external success followed by a local crash.

## Alpha completion rule

Do not call the build alpha-ready because individual classes or scripted harnesses pass. It is alpha-ready only when the packaged Windows app, fresh data root, real UI, production composition, real local model, TypeSafe test/live account, and sandbox/real read-only connectors complete the documented user scenarios and recovery gates.
