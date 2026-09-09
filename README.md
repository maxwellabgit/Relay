# Relay

Relay is a local-first Windows desktop orchestrator for your ideas, notes, projects, and approved agent work. Its core is **RELAY0**: a small local model that listens to the conversation streams you enable, judges continuously what matters, and turns that judgment into bounded tasks that deterministic software executes under policy. You never have to phrase a request for Relay to be useful, and nothing changes a project without your approval.

Relay owns the authoritative records: what was selected from a conversation, every note and where it came from, every decision, every task with its tool calls and tokens, and every change to itself. An AI proposes; software enforces permissions and executes; the ledger is append-only and hash-chained.

## One pipeline, several origins

Everything that happens in Relay is a **task**. Tasks arise from three origins and flow through one pipeline:

| Origin | How it starts | Authorization | Presentation |
| --- | --- | --- | --- |
| **Direct** | You asked (Ctrl+X, or the ask box while listening) | your request is intent; policy still gates every write | an answer is owed, always |
| **Observed** | RELAY0 noticed something in an enabled stream | only approved sources; nothing external without a grant | may be silent; a compact result, an alert, or a proposal |
| **Dialogue** | a follow-up to an earlier task (an external result came back, a proposal was edited) | inherits the parent's scope | attaches to the parent |

```
input ─► judge (RELAY0) ─► task (origin, kind, focused prompt, excerpt)
        ─► planner with read-only tools ─► answer + citations + knowledge state + proposals
        ─► policy (tier, validation, standing grants) ─► approval or automatic
        ─► executor (journaled, capability-bound) ─► arbiter (what the UI shows) ─► diagnostics record
```

Task kinds: **remember** (a note worth keeping), **check** (a stated fact against stored facts), **resolve** (an acronym, a name, a reference), **answer** (a direct question), **organize** (structural change to projects and notes), **research** (a bounded external task), **improve** (a change to Relay's own preferences, prompts, or routing).

The judge decides *whether* something is significant and *what kind* of task it is. It never executes. The planner calls read-only tools and may propose writes. The policy engine is the authority; model output is evidence, never authorization.

## Listening without recording

Ctrl+Alt starts and stops **listening** (Wispr Flow or typing supplies the words). Relay keeps a rolling **90-second buffer** of timestamped segments. The buffer expires continuously; it is never accumulated to the end of a session.

When the judge finds something significant, Relay persists an **excerpt**: the trigger sentence and the sentences that substantiate it (bounded, default 30 s), anchored to the decision that selected it. Overlapping excerpts reference the earlier one instead of copying text. A "nothing significant" check records metadata only (segment ids, hashes, tokens, latency) — never text. A **density guard** stops trigger-anchored excerpts from reconstructing the conversation: when retained excerpt time exceeds 25% of elapsed time, excerpts shrink to the trigger sentence.

The ledger follows the same rule. Segments enter it as hashes and excerpts as ids, and for any task that began from listening — and for its follow-ups — the judge's title, the plan summary and answer, tool arguments, and raw model output are recorded as fingerprints (length and a SHA-256 prefix), because each of them can quote the room. The words live in the task record under `tasks\`, beside the excerpt they cite, under the same retention. Direct asks are different: an instruction is your intent and is kept verbatim, and so is the answer you were given.

A direct question is available while listening. The ask box (or Ctrl+X) submits a direct task without interrupting the stream.

## What the UI shows, and what it does not

The UI manages attention; it is not an execution dashboard. An **arbiter** ranks significance, merges related findings, applies cool-downs and per-session budgets, and picks one of these levels for each task:

| Level | Meaning | Example |
| --- | --- | --- |
| none | metadata only, nothing displayed | an observed statement that matches stored facts |
| ambient | a subtle indicator | a note was filed under Atlas |
| result | a compact, persistent card | "CAD — computer-aided design (glossary)" |
| alert | a compact red card with both sources | "Atlas beta: heard the 21st; decided the 14th" |
| proposal | an editable card awaiting approval | "Move 3 backyard notes into Garden?" |
| findings | a concise summary with sources and limits | the result of an approved external research task |

Alerts need evidence: at least two retrieved sources and a confidence above the threshold; otherwise a possible inconsistency is a result, not an alert. Terms you asked to "always show" stay pinned and are refreshed in place, never re-carded. Direct questions are answered even when the answer is "consistent".

Every task carries **process tags** while it runs — Listening · Recalling · Planning · Extracting · Organizing · Editing · Searching Online · Asking *model* · Awaiting Approval · Applying · Completed/Failed — and each tag names the only permissions active in that lane. The **diagnostics drawer** shows each task's focused prompt, tool calls with arguments and results, model calls with tokens and latency, the policy decision, the presentation decision, and your response.

## Preferences

Preferences are typed and compiled, not prose in a prompt:

- **response style** — `minimalist` · `concise` · `normal`, compiled into the system prompt and generation limits
- **display** — terms to always show, alert budget per 10 minutes, cool-downs
- **filing** — standing grants such as "file Atlas decisions without asking" (grants are always approved by you and are revocable)
- **retention** — buffer seconds, excerpt bound, retained fraction
- **sources** — whether online search is allowed, which accounts are connected

"Update our preferences to always display concise text" is a direct task that yields two proposals: a change set to the prompt fragment and a preference record. Both are reversible change sets.

## Transformations

RELAY0 proposes the transformation that fits — file, move, merge, rename, archive, or **delete** when you asked for it. Deletion is an approved action, not an alias for archiving. Proposals with several operations form a graph with dependencies; partial approval respects them (approving "move into Garden" after rejecting "create Garden" is refused with the reason shown).

## Knowledge state and external work

Before delegating, the planner states a two-axis knowledge gap: *what is missing* (facts Relay has no source for) and *whether the local model can do it* (capability). A local lookup resolves the first without disclosure; the second justifies a bounded external task. An external task is a proposal that binds the exact package (which notes, how many characters, the destination model, the budget, whether search is allowed); Relay stores the response as a source artifact and shows a concise summary with its limits, then extracts notes and proposals separately.

## Improvement

"Improve" tasks are ordinary tasks with a stricter contract: concrete expected benefit, permissions required, implementation scope, and acceptance criteria. Approved changes to preferences, prompt fragments, or routing are **change sets** with a stored *before*, and can be reverted. Recorded task diagnostics feed an evaluation harness together with authored unseen and failure cases; recorded cases alone are never the whole evaluation set.

In mind mode (`docs/09`) improvement is mostly **tool building**: when a task needs something no tool provides (the time in another zone, say), the mind drafts a small JavaScript tool, the worker runs its tests in a sandbox that sees nothing of the machine except a closed set of host functions, and one approval promotes it as a change set. The tool is then available to every later task and can be reverted from the panel like any other change Relay made to itself.

## Security model

- The application owns the ledger, memory, project files, and self-changes. The model owns nothing.
- Proposals are decided by a deterministic tier table: automatic (staging or additive writes), requires approval, prohibited. Approval binds to the proposal hash; execution consumes a single-use, time-limited capability and re-checks policy against the current world.
- Every write is journaled; an interrupted write surfaces in Review at the next start.
- Workers run in a job object with a broker as their only file access; they write to staging; applying their output is a second approval.
- Model gateways: one local endpoint (loopback `http` allowed) and named external profiles (`https` only). API keys live in DPAPI; the ledger records sizes, tokens, timings, and destinations — never keys or prompt text.
- Observed tasks read only approved local sources. Online and account access require a preference grant or a per-task approval.

## Requirements

- Windows 11 (x64), [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0). No Visual Studio; Relay is an unpackaged WinUI 3 app with a self-contained Windows App SDK.
- **RELAY0 model.** Any OpenAI-compatible chat endpoint. The reference setup is [llama.cpp](https://github.com/ggml-org/llama.cpp) serving **Ministral 8B Instruct (Q4_K_M, ~5.2 GB)** on `http://127.0.0.1:8080/v1/chat/completions` with grammar-constrained JSON. Without a model Relay still runs: a labeled heuristic judge and the deterministic grammar handle what they can, and the UI says so.
- Wispr Flow for voice. Everything also works by typing.

```powershell
# llama.cpp server example (adjust paths)
llama-server -m Ministral-8B-Instruct-2410-Q4_K_M.gguf -c 16384 --port 8080 --jinja
```

The model needs a context window of at least 8k tokens: the mind's prompt with a task's transcript and tool results runs to 2–5k. Ollama serves every model with 2048 tokens unless told otherwise and silently drops the middle of a longer prompt (the system prompt), which turns a capable model into one that asks the user about everything. `tools\live-eval.ps1` derives an Ollama model with `num_ctx` set for its runs; for Relay itself create one the same way (`FROM <model>` / `PARAMETER num_ctx 8192` in a Modelfile, `ollama create relay-ministral -f Modelfile`) and name it in settings.

## Build, test, run

```powershell
dotnet build Relay.slnx
dotnet test tests/Relay.Tests/Relay.Tests.csproj
dotnet run --project src/Relay.Desktop/Relay.Desktop.csproj
```

The executable is `Relay.exe` under `src\Relay.Desktop\bin\Debug\net10.0-windows10.0.26100.0\` (`bin\x64\Debug\…` when built through `dotnet run`) with `Relay.Worker.dll` beside it. One instance per data root; a second launch brings the first forward. Override the data root with `RELAY_DATA_ROOT`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-smoke.ps1      # add -NoBuild to reuse the last build
```

The smoke test launches the real window against a throwaway data root and drives it the way you would — the chords, typing, the ask box, the Approve and Details buttons through UI Automation — and checks the ledger and the rendered text after every step: create and approve a project, listen while a decision is filed and an errand lands in the Inbox, ask a question mid-stream and see the result card, recall the decision, open the diagnostics drawer, close cleanly, and confirm that no overheard words reached the ledger. Screenshots, the UI Automation tree, and the ledger copy are written to `%TEMP%\relay-ui-smoke\<timestamp>`. It needs an interactive desktop for about a minute.

## Using it

1. **Point RELAY0 at a model** — *Settings → Model*: endpoint `http://127.0.0.1:8080/v1/chat/completions`, model name, no key needed for loopback. Choose *Judge: model*.
2. **Create a project** — *New project…* picks the folder; or say `create project Atlas` (Ctrl+X) and approve.
3. **Listen** — Ctrl+Alt. Talk. RELAY0 files decisions and tasks it hears under the project they belong to (ambient), pins definitions you asked for (result), and raises a compact alert when a stated fact contradicts a stored decision — with both sources and an editable proposal to update the record. Ctrl+Alt again stops; a brief consolidation reconciles what was selected.
4. **Ask while listening** — type into the ask box or press Ctrl+X: `what did we decide about the Atlas beta date?` The answer arrives as a result card and cites the note; the note cites the excerpt it came from, so the same passage is never listed twice.
5. **Delegate** — `research Lightshift's competitors and give me an implementation plan` produces a knowledge-state statement, then an external-task proposal that shows exactly what would leave the machine. Approve, and the findings come back concise with limits, plus separate proposals for notes.
6. **Shape Relay** — `always show what CAD means`, `file Atlas decisions without asking`, `keep responses concise`. Each is a reversible change set you approve.

## Settings

`%LOCALAPPDATA%\Relay\config\settings.json` (edited in *Settings*):

```json
{
  "schemaVersion": 2,
  "hotkeys": { "scope": "window", "noteKey": "Ctrl+Alt", "commandKey": "Ctrl+X" },
  "capture": { "transcriptTimeoutMs": 10000, "stabilizationMs": 600, "completedReceiptMs": 4000, "draftPersistDebounceMs": 200 },
  "stream": { "bufferSeconds": 0, "segmentQuietMs": 1200, "observeIntervalMs": 12000, "minIngestChars": 240, "minIngestSeconds": 20, "excerptMaxSeconds": 30, "maxRetainedFraction": 0.25 },
  "judge": { "mode": "model", "minConfidence": 0.55, "timeoutMs": 8000 },
  "orchestrator": { "mode": "rules+model", "maxSteps": 12, "planningTimeoutMs": 60000, "maxToolCalls": 8, "autoRouteThreshold": 0.75, "reviewThreshold": 0.35 },
  "model": { "enabled": true, "endpoint": "http://127.0.0.1:8080/v1/chat/completions", "model": "ministral-8b-instruct", "secretName": "model-gateway", "timeoutMs": 30000, "maxOutputTokens": 800 },
  "externalModels": [ { "name": "research", "endpoint": "https://api.openai.com/v1/chat/completions", "model": "gpt-5-nano", "secretName": "external-research", "maxOutputTokens": 4000 } ],
  "workers": { "enabled": true, "wallClockSeconds": 120, "memoryMb": 512, "maxToolCalls": 400, "maxReadBytes": 8388608, "maxWriteBytes": 2097152 }
}
```

Preferences live separately in `config\preferences.json` and change only through approved change sets.

## What is on disk

```
%LOCALAPPDATA%\Relay\
  ledger\relay-ledger.jsonl        everything that happened — hash-chained, append-only, no transcript text
  registry\projects.json           project identities, slugs, aliases, roots, per-project policy
  config\settings.json             settings          config\preferences.json   typed preferences
  config\secrets\*.bin             DPAPI-protected API keys
  config\prompts\*.md              prompt fragments (changed only through change sets)
  changesets\{id}.json             every self-change with its before image (revertible)
  tools\{name}.json                tools Relay built for itself: manifest, JavaScript source and tests in one file, each promoted by one change set
  staging\tools\{name}.json        drafts under test; staging\tools\runs\  one folder per sandboxed tool run
  excerpts\{id}.json               selected conversation excerpts, bounded, trigger-anchored
  tasks\{id}.json                  per-task diagnostics: prompt, tools, tokens, decisions, presentation, your response
  staging\stream\current.json      the live buffer window only (crash safety; expires with the buffer)
  staging\notes\ review\ proposals\ executions\ agents\    drafts, disputes, proposal records, write journals, worker runs
  artifacts\external\{id}.md       responses from approved external tasks (source artifacts)
  archive\  backups\  sessions\  incidents\

<project folder>\<slug>\
  project.toml  notes\ decisions\ tasks\ artifacts\
  .orchestrator\versions\          every previous version of every file Relay rewrote
```

## Testing environment

`dotnet test` runs deterministic tests in seconds against the real coordinator, stores, policy engine, executor, and task engine, with a fixed clock, a manual scheduler, a scripted judge, and scripted model replies. Scenario tests script whole workflows through the same surface the window uses:

```csharp
using var s = Scenario.New(tmp, judge: judge).WithWorkspace()
    .Command("create project Atlas").Approve()
    .Listen("We decided the Atlas beta ships on October 14.")     // observed → remember → filed (ambient)
    .Listen("Marketing wants the Atlas beta out on the 21st.")     // observed → check → alert + proposal
    .ExpectAttention(AttentionLevel.Alert)
    .Approve("supersede_note")
    .Ask("what did we decide about the Atlas beta date?")          // direct while listening
    .ExpectAnswerContains("21");
```

Failures print the transcript: segments, judge decisions, task lifecycle, tool calls, proposals, executions, presentation decisions, and ledger lines. Retention tests assert that the ledger holds no words from the stream — with the scripted judge and with the real heuristic one — and that retained excerpt time stays under the guard. Improvement tests apply a change set and revert it. A live evaluation runner (`RELAY_LIVE_MODEL`) scores a real model against the same cases.

## Repository layout

```
src/Relay.Core       pure .NET: stream buffer, judge contract, task engine, arbiter, preferences, change sets, policy, executor, ledger, memory, projects, workers
src/Relay.Gateway    the OpenAI-compatible HTTP client (loopback http or https, no redirects, no proxy)
src/Relay.Windows    Win32: window-scoped chords, foreground, ACL, single instance, DPAPI secrets, job-object worker host
src/Relay.Worker     sandboxed worker, JSON lines over stdio; runs built tools on Jint with the broker as the only way out
src/Relay.Desktop    WinUI 3 window, composition root
tests/Relay.Tests    xUnit: scenario DSL, scripted judge and model, fault injection, workers, evaluation cases
docs/                specifications
```

## Status

See `docs/08-build-plan.md` for the slice-by-slice status of this build.
