# Relay

Relay is a **local-first Windows desktop orchestrator**. A small local model — **RELAY0** — sits in one loop: it reads what just happened, takes one typed move, and deterministic software owns the consequence. You type, you talk, or the room talks; Relay files notes, answers questions, asks other AIs only with your approval, and builds tools for itself when none exist. Nothing leaves the machine, and nothing changes a project, unless you say so.

The model proposes. Software enforces permissions and executes. The ledger is append-only and hash-chained. The model owns nothing.

## One mind, one loop

Every input is an **observation** on the task: a typed ask, a stretch of overheard conversation, a tool result, a policy decision, an approval, a denial, a streamed fragment from another AI, your reply. Each step the mind returns one schema-constrained move — `say`, `use_tool`, `propose`, `delegate`, `build`, `ask_user`, `wait`, `stop` — plus a one-line read of the situation and a one-sentence **feed** line for you.

```
observation ──► mind (read + move + feed)
                 │
                 ├─ Decider (route, risk, filing…) and PolicyEngine
                 ├─ the move's consequence (tool, card, package, sandbox…)
                 └─ what happened, appended ──────────────────────────► next step
```

There are no separate roles and no mode you have to pick. Local first: Relay answers from notes, tools, and the model's own knowledge. If that is not enough it **offers** a delegate or a new tool; you decide. The loop never guesses what its last move did — the deterministic code tells it.

The contract, the feed UI, and the slice plan live in [`docs/09-orchestrator-rebuild.md`](docs/09-orchestrator-rebuild.md). The base that does not change — ledger, fingerprinting, path guard, projects, notes, search, executor, worker sandbox, gateway, change sets, evaluation harness — is described there under *Untouched base*.

## What you can do

- **Listen** without recording the room. Ctrl+Alt starts a conversation buffer. RELAY0 reads stretches of talk, not every fragment. What is kept is a bounded **excerpt** (trigger plus the sentences that substantiate it). The ledger holds hashes and ids, never the words. A density guard stops excerpts from reconstructing the conversation.
- **Ask.** The input box is typing first; Wispr Flow (or any Windows dictation) is one more typist. Ctrl+X focuses it. Direct questions are always answered.
- **File and transform.** Notes go to a named project and type. Moves, merges, archives, and deletes are proposals. Deletion is never an alias for archiving.
- **Delegate.** The mind writes the prompt. The package that would leave is exact, hashed, and shown to you. Replies stream back and are **digested** into at most three feed lines; the whole artifact sits behind them. One approval covers a short conversation (follow-ups under `reply_to`, no new local sources). **Retry locally** is always on the card.
- **Build a tool.** When a task needs something no tool provides, the mind names a contract. Relay drafts JavaScript, tests it in a sandbox that sees no files, network, or process (only declared `relay.*` host functions), and asks once at **promote**. The tool is a revertible change set. The acceptance path is a world clock: "What time is it in Tokyo?" ends with a promoted tool and the real local time.

## Privacy and authority

- Observed work may read only approved local sources. Online search and external models need a grant or a per-task approval.
- API keys live in DPAPI. The ledger records sizes, tokens, timings, and destinations — never keys or prompt text.
- Approval binds to the proposal hash. Execution consumes a single-use, time-limited capability and re-checks policy against the current world.
- Workers run in a job object; the broker is their only way out. Built tools inherit that sandbox.
- Preferences are typed records and revertible change sets, not prose stuffed into a prompt.

## Requirements

- Windows 11 (x64), [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0). Unpackaged WinUI 3, self-contained Windows App SDK. No Visual Studio required.
- **RELAY0:** any OpenAI-compatible chat endpoint. The reference is [llama.cpp](https://github.com/ggml-org/llama.cpp) or Ollama serving **Ministral 8B Instruct (Q4_K_M)** on loopback. The context window must be at least **8k tokens** (the mind's prompt plus a task's transcript runs to 2–5k). Ollama defaults to 2048 and silently drops the middle of a longer prompt — which is the constitution. `tools\live-eval.ps1` derives a model with `num_ctx` for its runs; for Relay itself:

```powershell
# llama.cpp
llama-server -m Ministral-8B-Instruct-2410-Q4_K_M.gguf -c 16384 --port 8080 --jinja

# Ollama (once): FROM <model> / PARAMETER num_ctx 8192 in a Modelfile
ollama create relay-ministral -f Modelfile
```

Without a model Relay still runs: a labeled heuristic and the older grammar handle what they can, and the UI says so.

## Build, test, run

```powershell
dotnet build Relay.slnx
dotnet test tests/Relay.Tests/Relay.Tests.csproj
dotnet run --project src/Relay.Desktop/Relay.Desktop.csproj
```

`Relay.exe` is under `src\Relay.Desktop\bin\Debug\net10.0-windows10.0.26100.0\` (`bin\x64\Debug\…` when launched through `dotnet run`) with `Relay.Worker.dll` beside it. One instance per data root; a second launch brings the first forward. Override the data root with `RELAY_DATA_ROOT`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\live-eval.ps1   # needs RELAY_LIVE_MODEL_KEY (any value on loopback)
```

`dotnet test` is deterministic: real coordinator, stores, policy, executor, and loop against a fixed clock, a manual scheduler, and scripted minds. Failures print the session transcript. Live tests return immediately unless the key is set.

## Using it

1. **Point RELAY0 at a model** — *Settings → Model*: loopback chat-completions URL, model name, no key. Set orchestrator mode to **mind**.
2. **Create a project** — *New project…*, or type `create project Atlas` and approve.
3. **Listen** — Ctrl+Alt. Talk. Relay files what it can under the project it belongs to, and raises a card when a stated fact contradicts a stored decision. Ctrl+Alt again stops.
4. **Ask** — type, or Ctrl+X: `what did we decide about the Atlas beta date?`
5. **Delegate** — `research Lightshift's competitors and give me an implementation plan`. The card shows exactly what would leave. Approve, or **Retry locally**.
6. **Shape Relay** — `always show what CAD means`, `file Atlas decisions without asking`, `keep responses concise`. Each is a reversible change set. `What time is it in Tokyo?` is how it grows a tool.

## Settings

`%LOCALAPPDATA%\Relay\config\settings.json` (also *Settings* in the window):

```json
{
  "schemaVersion": 2,
  "hotkeys": { "scope": "window", "noteKey": "Ctrl+Alt", "commandKey": "Ctrl+X" },
  "stream": { "bufferSeconds": 0, "observeIntervalMs": 12000, "minIngestChars": 240, "minIngestSeconds": 20, "excerptMaxSeconds": 30, "maxRetainedFraction": 0.25 },
  "orchestrator": { "mode": "mind", "maxSteps": 12, "planningTimeoutMs": 60000, "maxToolCalls": 8 },
  "model": { "enabled": true, "endpoint": "http://127.0.0.1:8080/v1/chat/completions", "model": "ministral-8b-instruct", "secretName": "model-gateway", "timeoutMs": 30000, "maxOutputTokens": 800 },
  "externalModels": [ { "name": "research", "endpoint": "https://api.openai.com/v1/chat/completions", "model": "gpt-5-nano", "secretName": "external-research", "maxOutputTokens": 4000 } ],
  "workers": { "enabled": true, "wallClockSeconds": 120, "memoryMb": 512 }
}
```

`orchestrator.mode`: `mind` is the loop above; `rules+model` is the older planner still in the tree while that loop is evaluated. Preferences live in `config\preferences.json` and change only through approved change sets. `stream.bufferSeconds` 0 holds the whole conversation for the session; 15–600 restores a rolling window.

## What is on disk

```
%LOCALAPPDATA%\Relay\
  ledger\relay-ledger.jsonl        hash-chained, append-only, no transcript text
  registry\projects.json           identities, slugs, aliases, roots, per-project policy
  config\settings.json             settings
  config\preferences.json          typed preferences (change sets only)
  config\secrets\*.bin             DPAPI-protected API keys
  config\prompts\*.md              mind / build / digest fragments (change sets only)
  config\decisions.json            route, filing, risk weights (change sets only)
  changesets\{id}.json             every self-change with its before image
  tools\{name}.json                promoted tools (manifest + source + tests)
  staging\tools\                   drafts under test
  excerpts\{id}.json               bounded, trigger-anchored conversation excerpts
  tasks\{id}.json                  per-task diagnostics
  artifacts\external\              approved external replies (source artifacts)
  archive\  backups\  sessions\  incidents\

<project folder>\<slug>\
  project.toml  notes\ decisions\ tasks\ artifacts\
  .orchestrator\versions\          previous versions of every file Relay rewrote
```

## Repository

```
src/Relay.Core       ledger, mind, loop, policy, executor, tools, decisions, storage
src/Relay.Gateway    OpenAI-compatible HTTP client (loopback http or https)
src/Relay.Windows    chords, single instance, DPAPI, job-object worker host
src/Relay.Worker     sandboxed worker; Jint for built tools
src/Relay.Desktop    WinUI 3 window
tests/Relay.Tests    xUnit: scenario DSL, scripted mind, evaluation cases
docs/                specifications (09 is the current intent)
```

## Status

The product intent is [`docs/09-orchestrator-rebuild.md`](docs/09-orchestrator-rebuild.md). Slices 1 (mind + loop), 5 (delegation, digest, bounded multi-turn), and 6 (tool building) have landed and been run live against Ministral 8B. The feed UI, grammar removal, and state-machine collapse are still ahead. Earlier slice status: [`docs/08-build-plan.md`](docs/08-build-plan.md).
