# Orchestrator Foundation v0.1

Status: product and security contract  
Date: 2026-09-04

## 1. Purpose

Build a private desktop control surface for one persistent orchestrator agent. The orchestrator captures notes, manages project knowledge and folders, recalls earlier ideas, proposes work, and coordinates bounded worker agents. It is intentionally neutral: no simulated personality, ambient companionship, or unnecessary conversation.

The application—not a model provider, chat client, note application, or Wispr Flow—owns the authoritative record, policy, state, and user interface.

## 2. Locked product decisions

1. Wispr Flow supplies speech-to-text.
2. There are two primary global keyboard toggles:
   - `NOTE_KEY`: start or stop silent note capture.
   - `COMMAND_KEY`: open or close an instruction turn with the orchestrator.
3. Both modes terminate only through their initiating toggle, an explicit visible Cancel control, or a clearly reported failure.
4. Note mode never generates a conversational response.
5. Command mode begins with a neutral `Ready` greeting and exposes the complete instruction lifecycle.
6. The product uses a native desktop control surface, not a browser-hosted UI.
7. The application is local-first. Third-party services are replaceable adapters, not sources of truth.
8. Every orchestrator action is visible, attributable, and recoverable where technically possible.
9. The language model may propose actions. Deterministic application code decides whether they are valid and whether approval is required.
10. No permanent deletion exists in the first release.
11. The first release targets Windows 11.
12. The control surface uses packaged WinUI 3 on .NET 10. It contains no embedded browser or web application.

## 3. Interaction contract

### 3.1 Note mode

`NOTE_KEY` from Idle:

1. Display the persistent `CAPTURING NOTE` indicator.
2. Focus an isolated, initially empty capture surface.
3. Start Wispr Flow hands-free dictation through the Flow adapter.
4. Accept only text inserted into this isolated surface.
5. Show elapsed time and whether text has arrived. Do not display project data around the capture field.

`NOTE_KEY` while capturing:

1. Stop Wispr Flow.
2. Display `WAITING FOR TRANSCRIPT` while Flow finishes.
3. Preserve the verbatim inserted text as a capture event.
4. Display `ORGANIZING NOTE` while local validation and agent extraction run.
5. Write proposed note records with their source capture ID.
6. Route high-confidence notes to an existing project; place uncertain routing in Review.
7. Return to Idle.

The orchestrator must not answer, summarize aloud, open a conversation, or interrupt with follow-up questions in Note mode. A small visual receipt may say what was stored, for example: `Saved 2 notes · 1 routing decision needs review`.

### 3.2 Command mode

`COMMAND_KEY` from Idle:

1. Open the control surface.
2. Display the neutral greeting `Ready` and a distinct command-mode indicator.
3. Focus the isolated capture surface.
4. Start Wispr Flow hands-free dictation.

`COMMAND_KEY` while capturing:

1. Stop Wispr Flow.
2. Display `WAITING FOR TRANSCRIPT` until insertion completes or times out.
3. Preserve the verbatim instruction as an event.
4. Display the orchestrator plan before any approval-gated action.
5. Execute safe read-only work or request approval for controlled actions.
6. Present the response, sources, proposed changes, and complete action history in the same surface.

The v0.1 greeting is visual. Spoken greetings and text-to-speech are outside the first vertical slice because they can be captured by the microphone and complicate mode clarity.

### 3.3 Mode collisions

Only one capture mode can exist at a time.

If the other primary key is pressed during capture, the application does not switch modes or submit the existing text. It displays: `Finish or cancel the active capture first.` The active mode and its initiating key remain visible.

### 3.4 Cancellation and failure

- A visible Cancel control is always present during capture.
- Escape cancels only while the control surface is focused; it is not a third global hotkey.
- Cancellation never submits partial text.
- A cancelled attempt is recorded as metadata without storing its partial content unless the user chooses `Recover draft`.
- Flow timeout, insertion failure, loss of focus, and connectivity failure each produce distinct states and recovery choices.
- The application does not read the global clipboard as a transcript fallback in v0.1.
- If Flow reports or appears to use clipboard fallback, the application tells the user to recover the last transcript from Flow. It does not silently import clipboard contents.

## 4. Visible application states

The UI must always show exactly one primary state:

| State | Meaning | User actions |
| --- | --- | --- |
| `IDLE` | No capture or work is active | Start Note or Command |
| `NOTE_CAPTURE` | Flow is recording a silent note | Stop, Cancel |
| `COMMAND_CAPTURE` | Flow is recording an instruction | Stop, Cancel |
| `AWAITING_TRANSCRIPT` | Flow stopped; insertion is pending | Cancel, Retry after timeout |
| `ORGANIZING` | A note is being extracted and routed | Inspect activity |
| `PLANNING` | The orchestrator is interpreting an instruction | Inspect activity, Cancel |
| `AWAITING_APPROVAL` | One or more proposed actions are blocked | Approve, Edit, Reject |
| `EXECUTING` | Approved or automatically permitted work is running | Inspect activity, Stop safely |
| `COMPLETED` | Work completed and results are available | Inspect, Return to Idle |
| `FAILED` | Work stopped without completing | Inspect error, Retry, Return to Idle |
| `LOCKED` | Integrity or policy protection stopped the system | Inspect incident, explicitly unlock |

Colors may reinforce states but cannot be their only indicator. State label, mode, action, target, and elapsed time remain visible.

## 5. Flow integration boundary

Wispr Flow is used only to transform microphone audio into text. It does not own notes, projects, conversation history, or commands.

Flow currently provides a configurable hands-free toggle and supports multiple shortcut bindings, but does not provide an ordinary dictation-history API suitable for this application. The first adapter therefore uses a configured private Flow shortcut:

1. The application receives `NOTE_KEY` or `COMMAND_KEY`.
2. It creates and focuses the isolated capture surface.
3. It invokes one fixed, user-configured Flow hands-free shortcut.
4. It watches only its own capture control for text changes.
5. The second press invokes the same fixed Flow shortcut to stop.
6. It waits for a stable text insertion before submitting the capture internally.

The adapter may emit only the configured Flow shortcut. It cannot inject arbitrary keystrokes. The configured key combination, Flow process identity, focused control identity, focus changes, and transcript arrival are shown in diagnostics.

Flow Context Awareness should be disabled for this application. The capture window contains no neighboring notes, project names, instructions, credentials, or activity history that Flow could use as context.

## 6. Primary interface

The UI has four permanent regions, even if three are collapsed while idle:

1. **Status** — current state, mode, elapsed time, connectivity, and active project.
2. **Capture/response** — the isolated transcription surface or current orchestrator response.
3. **Activity** — ordered, plain-language events for every internal decision and attempted action.
4. **Review** — proposed project routing, file changes, agent launches, and approvals.

The application never hides background work behind a spinner. A long-running state expands into concrete events such as:

- `Stored source capture`
- `Identified 3 candidate notes`
- `Matched 2 notes to Project Atlas`
- `Routing confidence too low for 1 note`
- `Proposed creation of project folder /projects/new-market-study`
- `Waiting for approval`

Every displayed summary links back to its source event or proposed change.

## 7. Authority model

### Automatically allowed

- Create an append-only capture event.
- Read project registry and authorized project files.
- Search indexed notes and transcripts.
- Create draft notes in the application staging area.
- Add search-index entries derived from an existing authorized source.
- Generate a proposed plan or proposed file diff.
- Perform local health checks.

### Requires approval

- Create a formal project folder.
- Modify a canonical project note or decision.
- Rename or move a project or canonical artifact.
- Launch a worker agent.
- Give a worker network access.
- Give a worker access to more than one project.
- Apply a worker-produced patch.
- Export or transmit local content.
- Add a new tool or integration.

### Prohibited in v0.1

- Permanent deletion.
- Sending messages, publishing, deploying, purchasing, or changing accounts.
- Arbitrary shell execution by the orchestrator.
- Reading outside explicitly registered workspace roots.
- Reading the clipboard, browser history, screen contents, email, or calendar.
- Adding tools dynamically at a model's request.
- Allowing workers to write directly to canonical storage.

## 8. Action protocol

The model never receives a raw filesystem or shell handle. It emits a structured proposal:

```json
{
  "proposal_id": "uuid",
  "action": "create_project",
  "reason": "User explicitly requested a new project",
  "target": {"slug": "market-study"},
  "source_event_ids": ["uuid"],
  "expected_effects": ["Create one staged project directory"],
  "risk": "controlled_write",
  "requires_approval": true
}
```

The policy engine validates the schema, target, path boundary, state preconditions, approval requirement, and expected effects. The executor receives a short-lived capability for that exact action. Any change to the target or effects invalidates approval.

## 9. Authoritative data model

The application maintains:

- `events`: immutable captures, user instructions, model outputs, proposals, approvals, executions, errors, and recoveries.
- `projects`: stable project IDs, names, aliases, status, roots, and policy.
- `notes`: atomic facts, ideas, questions, decisions, references, and tasks.
- `sources`: links from every derived note to exact event spans.
- `proposals`: structured requested actions and their lifecycle.
- `agent_runs`: bounded worker assignments, capabilities, outputs, and resource use.
- `artifacts`: registered files with hashes, ownership, status, and version.

The event ledger is authoritative for what happened. Project folders and notes are authoritative for user content. Search indexes and generated summaries are disposable projections that can be rebuilt.

## 10. Project storage contract

A project is identified by an immutable UUID rather than its folder name. Its initial folder contains:

```text
project-root/
  project.toml
  overview.md
  notes/
  decisions/
  tasks/
  conversations/
  artifacts/
  .orchestrator/
```

`.orchestrator/` contains local metadata, versions, source links, and staging records. The model cannot write it directly.

Deleting a project in early versions means moving it to a recoverable archive after explicit approval. The ledger records the old path, new path, file manifest, hashes, approval, and recovery deadline.

## 11. Memory rules

1. Preserve the raw user transcript before interpretation.
2. Never overwrite source material with a cleaned transcript.
3. Every note contains project ID, type, source spans, creation time, confidence, and status.
4. Low-confidence routing is visible in Review; it is not silently assigned.
5. Conflicting memories coexist and are marked disputed until resolved.
6. A new conclusion may supersede an old one but cannot erase it.
7. Recall responses cite their originating conversation or file.
8. Search combines exact terms, metadata filters, and semantic similarity. The semantic index is never treated as ground truth.

## 12. Security architecture

- Native desktop process; no browser-hosted application and no remotely reachable local web server.
- Local IPC uses an operating-system authenticated channel such as a named pipe or Unix-domain socket.
- Default-deny network policy. Only the model gateway and Flow have network access in the initial release.
- Credentials remain in the operating-system credential store and are never inserted into prompts, logs, or project folders.
- Workspace roots use normalized canonical paths. Symlink, junction, traversal, and case-folding escapes are rejected.
- Executors use typed operations, never model-generated shell strings.
- Worker agents run in isolated staging directories with read-only task inputs and no network by default.
- Canonical changes are applied only by the trusted executor after validation and, where required, approval.
- Ledger records are append-only and hash-linked for tamper evidence.
- Sensitive log fields use explicit redaction rules; silent heuristic redaction is insufficient.
- Updates are signed. Automatic updates remain disabled until the update chain has its own threat model.
- Crash recovery replays only committed events. It never repeats an external side effect merely because the UI lost connection.

## 13. Worker-agent contract

The orchestrator is the only durable agent identity. Workers are temporary processes with no independent long-term memory.

Each worker receives:

- one task ID and objective;
- a finite input bundle;
- an explicit file allowlist;
- a tool allowlist;
- network disabled unless separately approved;
- time, token, and cost limits;
- a writable staging directory;
- required output and verification criteria.

Workers return reports, patches, and artifacts to staging. They cannot update project memory or canonical files. The orchestrator validates, summarizes, presents, and requests approval to apply their work.

## 14. Windows implementation baseline

The first Windows solution contains four projects:

- `Orchestrator.Desktop`: packaged WinUI 3 application, state presentation, capture surface, global-hotkey registration, and Flow adapter.
- `Orchestrator.Core`: platform-neutral state machine, commands, events, policy types, and validation. It has no UI, model, network, or filesystem implementation dependencies.
- `Orchestrator.Windows`: narrow wrappers around approved Windows APIs for process identity, foreground-window control, secure credential access, filesystem boundaries, and eventual named-pipe IPC.
- `Orchestrator.Tests`: transition-table, recovery, policy, serialization, and adapter-contract tests.

Initial implementation rules:

- Target .NET 10 and the current supported Windows App SDK release installed by the official Visual Studio WinUI template.
- Package with MSIX. Development certificates are acceptable only for development builds; release artifacts require a protected signing key.
- Use the Win32 `RegisterHotKey` API for the two primary global toggles. Do not install a global low-level keyboard hook.
- Recommend `F13` for `NOTE_KEY` and `F14` for `COMMAND_KEY` because they are rarely claimed by ordinary applications. Both remain configurable.
- Configure Flow Hands-free Mode to a private three-key binding reserved for the adapter, provisionally `Ctrl+Win+F24`.
- The Flow adapter may emit only that exact configured chord and only after verifying its own capture control is foreground and empty.
- No component may expose a localhost TCP port.
- Keep the first slice in one signed desktop process. Introduce a separately privileged executor over an ACL-restricted named pipe only when controlled file writes are added.
- Store the first ledger as append-only newline-delimited JSON with sequence numbers, UTC timestamps, prior-record hashes, and explicit flush-to-disk behavior. A database arrives only when indexing and projections require it.
- Store configuration beneath the per-user application data directory with ACL inheritance disabled and access limited to the current user and SYSTEM.
- Store secrets in Windows Credential Manager or protect them with DPAPI; never serialize secrets into the ledger.
- Log the existence and result of a Flow relay action, but never log synthetic key events unrelated to the fixed relay chord.

## 15. First vertical slice

The first runnable build does only the following:

1. Starts as a signed native desktop application.
2. Registers `NOTE_KEY` and `COMMAND_KEY`.
3. Shows the explicit state panel.
4. Opens an isolated capture control.
5. Starts/stops Wispr Flow using a fixed configured adapter shortcut.
6. Detects transcript insertion without reading the global clipboard.
7. Appends the exact capture and state transitions to a local ledger.
8. In Note mode, stores a draft note without model interpretation.
9. In Command mode, shows the captured instruction but performs no tools.
10. Recovers cleanly from cancellation, timeout, focus loss, and application restart.

No project routing, LLM call, semantic memory, worker agent, file mutation, or deletion belongs in this slice. This isolates the riskiest interface dependency before intelligence is added.

## 16. Build sequence

1. **Control surface:** state machine, hotkeys, Flow adapter, capture field, activity ledger.
2. **Local records:** encrypted metadata store, project registry, source-backed notes, backup and recovery.
3. **Read-only orchestrator:** command interpretation, search, recall, visible plans, no mutations.
4. **Controlled writes:** typed project and note operations with diffs and approval.
5. **Worker sandbox:** one local worker type, staging-only output, approval-gated application.
6. **Memory refinement:** note extraction, routing confidence, contradictions, supersession, and source-based recall.
7. **Additional integrations:** added individually only after threat modeling and explicit enablement.

## 17. Decisions still required

1. Confirm or replace the provisional `F13` and `F14` physical key mappings.
2. Decide whether `Ready` is visual only or a local earcon plus text. Spoken output remains deferred.
3. Confirm that the narrowly limited synthetic Flow shortcut is acceptable, given that Flow does not expose a general dictation API.
4. Decide whether project notes remain normal BitLocker- and ACL-protected files or require an application-encrypted vault.
5. Choose which external model endpoint, if any, may receive command and note text during the read-only orchestrator phase.

## 18. Acceptance criteria for the first slice

- One press always starts the selected mode; the second press always attempts to stop the same mode.
- The two modes cannot overlap or silently convert into each other.
- The UI state never contradicts the actual capture state.
- No transcript can be submitted under the wrong mode.
- Every state transition and failure is visible and logged.
- Note mode produces no conversational response.
- Command mode begins with `Ready` and preserves the exact instruction.
- Loss of network, focus, Flow, or application process cannot create an unlogged canonical change.
- Restart returns to a safe state and clearly exposes any recoverable interrupted capture.
- No application component can permanently delete a user project.
