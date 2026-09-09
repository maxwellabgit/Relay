# 09 · The orchestrator rebuild

The skeleton built so far (phases 1–6, the RELAY0 reformalization) encoded a misunderstanding: a dictation-capture state machine as the product's spine, three model roles behind a regex classifier, and a command grammar as the primary planner. This document is the plan that replaces that spine while leaving the base untouched. It is the reference the slices are built against; the answers the user gave when the plan was reviewed are folded in.

## The corrected model

Relay is **one local mind running one loop**. Every input — a typed ask, a stretch of overheard conversation, a tool result, a policy decision, an approval, a denial, a streamed fragment from another AI, a user's reply — is an **observation** appended to the task's transcript. Each step, the mind reads the transcript and returns **one typed move** with a one-line **read** of the situation and a one-sentence **feed** line for the user. Deterministic code owns every consequence: it runs tools, applies policy, asks for approval, executes, logs, and decides between paths with tunable weights. Then it appends what happened as the next observation, and the loop continues. There are no modes, no roles, no separate boxes: one feed, one input, inline decisions.

The local model (Ministral today) is a placeholder for any AI. What makes it the orchestrator is the contract, not the weights.

## The self-observation loop

```
observation ──► MIND step (one schema-constrained call: read + move + feed)
                 │
                 ├─ deterministic gates: Decider (route, fof, retry…), PolicyEngine
                 │
                 ├─ move handlers own the consequence:
                 │    say       → feed / answer                       use_tool → ToolBroker
                 │    propose   → policy → approval → Executor         delegate → DelegateRuntime (SSE)
                 │    build     → ToolBuilder (draft/test/promote)     ask_user → inline question
                 │    wait      → nothing to do (terminal)             stop     → cancel the outstanding operation
                 │
                 └─ what happened is appended as an observation ─────────────────────► next step
```

Rules of the loop (`Relay.Core/Mind/TaskLoop.cs`):

- **Every consequence is observed.** A tool returns → `tool` observation. A proposal is decided → `policy` observation (`allowed` / `needs_approval` / `denied`, with the engine's reasons). The user approves or rejects → `approval` observation. An operation executes → `executed` observation with the result. A delegate streams → `delegate.partial` observations while it runs, then `delegate.returned`. A build is drafted, tested, promoted → `build` observations. A budget or contract problem → `system` observation. The mind never has to guess what its last move did.
- **Waiting is explicit.** When a move's consequence is not immediate (approval needed, user asked, delegate or build running) the loop stops stepping and records what it waits for. The host resumes it with the observation that ends the wait. Partial observations (a streaming delegate) resume the loop for **one** step so the mind can narrate, keep waiting, or `stop`; the host throttles how often partials are surfaced (a decision weight, not a constant).
- **Terminal moves.** `say` with `done: true` ends the task with an answer; `wait` with nothing outstanding ends it with no action (the usual result for an overheard window that meant nothing). Budgets (steps, tool calls, consecutive narration, schema retries) end it visibly when the mind does not.
- **The transcript is the memory.** The mind sees the whole task so far, rendered compactly, on every step. Nothing about the loop's state lives in the model.

## The contract (one schema, every step)

```json
{
  "read": {
    "intent": "one line",
    "complexity": 0.0,
    "needs": ["none | local_notes | world_knowledge | new_tool | external_reasoning | user_input"],
    "significance": 0.0,
    "sensitivity": 0.0,
    "risk": { "core": 0.0, "security": 0.0, "loop": 0.0, "destructive": 0.0 }
  },
  "move": { "type": "say | use_tool | propose | delegate | build | ask_user | wait | stop",
            "text": "", "name": "", "args": { "k": "v" }, "done": false },
  "feed": "One plain sentence about what is happening now."
}
```

The move is deliberately flat — five fields, always present — so it is strict-schema compatible on any OpenAI-style host and a small grammar on llama.cpp. Each type reads the fields its own way: `use_tool` (name = tool, args), `propose` (name = action, args = target, text = reason), `delegate` (name = profile, text = the prompt the mind wrote, args = refs / budget_tokens / allow_search), `build` (name = tool name, text = one-line justification, args = inputs / outputs), `ask_user` (text = question, args.options), `say` (text, done), `wait` / `stop` (text = reason). `read` is the model's own triage; the deterministic **Decider** turns it into routes with tunable weights.

Utility prompts, not roles: `build.md` (the fixed tool-build prompt) and `digest.md` (turn an external AI's reply or reasoning into ≤3 feed lines). The delegate prompt itself is authored by the mind per task. `config/prompts/mind.md`, when present, replaces the mind's constitution; the dynamic sections (tools, actions, profiles, preferences) are always appended.

## Decisions and usage data

Every choice between paths goes through `Decider.Decide(spec, features)` with weights loaded from `config/decisions.json` (a change set, so revertible). Initial specs: `route` (local / offer_delegate / offer_build / ask_user), `fof` (allow / approval / refuse), `filing` (auto / ask / inbox — the mind names the note type and project; the filing decision is an approval when its confidence is under the threshold, 0.75 to file automatically, 0.35 to ask, otherwise the inbox), `attention` (how an observed finding is presented), `retry` (schema and transport failures), `narrate` (how often a streaming delegate's partials are surfaced). Every decision is written to the ledger as `decision.made` with features, weights, score and outcome, and every task writes a usage line (`usage/{date}.jsonl`) with the route taken, scores, model metrics and the user's response — including overrides, which are the labels for later tuning.

## The fundamental-operation flag

Two layers. **Hard** rules are deterministic (PolicyEngine tiers, the tool manifest validator, the job object): no ledger access, no writes outside staging before promotion or outside a declared project after, no process spawn, no sockets except declared hosts under approval, no secrets, no edits to policy or to the safety-class decisions, mandatory wall-clock and instruction budgets, memory cap. **While the architecture is being built and tested these are loosened** where there is something to loosen: the sandbox of slice 6 turned out to need no loosening because its rules are structural (Jint sees no file system, network or process; the host catalog is closed), so `decisions.json` carries the `development` switch (true today) for the day host functions grow beyond the clock — a brokered fetch, writes into a declared project — and the validator warns instead of refusing under it; tightening is then a flip, not a rewrite. **Soft** rules use the mind's `read.risk`: for `build` and self-directed `propose` moves the Decider maps risk to allow / approval / refuse with the rationale on the feed. The model can raise the bar, never lower it below the hard rules.

## Delegation

`ExternalRuntime` becomes `DelegateRuntime`. The `delegate` move carries the prompt the mind wrote for the task plus the local references it selected; the package is still exact, still hashed, still approved. Replies stream over SSE; the loop surfaces partial observations (throttled) so the mind can narrate or `stop`; the full reply is stored as an artifact and digested into feed lines through `digest.md`. **Bounded multi-turn** in the first cut: the mind may reply to the delegate within the approved budget; each round is observed. Delegation is raised to the user as a proposal after each failed local attempt, with a retry option. OpenAI-compatible endpoints only.

## Capability building

The `build` move names a tool, a one-line justification, inputs and outputs. `ToolBuilder` fills the fixed `build.md` prompt with that contract and asks the local model for a **package** under a schema: one JSON file holding the manifest (name, description, arguments, the host functions the source may call), the JavaScript source (`function run(args)`) and its tests (arguments plus what the result must contain). The package is validated before anything runs (snake_case name not already taken, bounded arguments and source, host functions from the closed catalog only, at least one test whose arguments are all declared), saved as `staging\tools\{name}.json`, and its tests run in the worker on **Jint** (JavaScript in-process in `Relay.Worker`, inside the existing job object): a statement cap, a wall clock, a recursion limit, a memory limit, and no file system, network or process at all — the only way out is `relay.*`, each call of which is a brokered `host` tool call that the package must have declared (`time.now`, `time.zone`; adding a function is a code change, never something a build can do). One retry feeds the failure back verbatim. Draft → Test → **Promote (the one approval, `add_tool`, pinned to the tested source hash)** → Use → Retire. Promotion moves the package to `tools\{name}.json` as one change set of kind `tool`, so reverting it from the panel removes the tool; the suspended task observes the promotion, its tool list is refreshed, and `use_tool` runs the promoted tool in the same sandbox. The acceptance scenario is the world clock: "What time is it in Tokyo?" ends with a promoted `world_clock` tool and the answer. Proposing to delegate a build after a failed local attempt lands with slice 5.

## The feed

One status line, one chronological feed, one input box — typing first; Wispr Flow is one more typist and any Windows dictation works the same way. Ctrl+Alt toggles listening; Ctrl+X focuses the input; Enter asks. Every step is one feed row (the mind's `feed` sentence, task colour, time), expandable to its evidence (tool arguments and results, package contents, decision scores, external digest). Proposals are inline-approvable rows. Response / Attention / Review / Tasks stop being separate boxes; projects, inbox, self-changes and the raw ledger become drawers.

## Conversation mode

Listening holds **the whole conversation** for the session (`stream.bufferSeconds = 0`, the default; 15–600 restores the rolling window) and ingests on a slower cadence: the judge is offered new talk every `stream.observeIntervalMs` (default 12 s) but a pass only runs once at least `stream.minIngestChars` (240) new characters have gathered or the oldest unjudged segment is `stream.minIngestSeconds` (20) old — so it reads a stretch of conversation, not each fragment. A watched term and the stop of the stream never wait. Nothing is removed until it is clear what can be removed easily; the ledger rule is unchanged (no words, only fingerprints and excerpt ids), the excerpt store remains the only place words outlive the buffer, and the density guard still applies to what is retained. (Slice 1 keeps the judge as the reader of the stream; slice 3 steps the mind over it instead.)

## Grammar removal and the fast path

Deleted: `RuleBasedOrchestrator`, `CompositeOrchestrator`, `HeuristicJudge`, the direct classifier, `NoteExtractor`'s cue regexes, `NoteRouter`'s weighting, `ModelJudge`, `ModelOrchestrator`. Kept: `SearchIndex.Tokenize`, sentence splitting. In their place `IFastPath.Try(input, context)` and a `FastPathRegistry` consulted before the mind — empty at first, logged as `fastpath.hit` / `fastpath.miss` — the foundation for future lookup tools that improve response time without deciding anything.

## State machine collapse

`RelayState` becomes `Starting, Ready, Failed, Locked`; listening is a flag on `Ready`. The capture/draft/stabilization machinery is removed; the input box saves its text crash-safe. The buffer, segmenter, excerpts, density guard and `Withheld` stay.

## Untouched base

`FileLedger` and its verify/repair, `Withheld` fingerprinting, `PathGuard` / `DataRoot` / `AtomicFile`, projects / notes / spans / `SearchIndex`, the typed `Executor` with journal recovery, `WorkerRuntime` / `WorkerBroker` / the job object, `OpenAiCompatibleClient` and DPAPI secrets, `ChangeSets`, the Scenario and evaluation harness. `PolicyEngine` and `Proposal` are extended, not replaced.

## Slices

| # | Slice | Lands |
| --- | --- | --- |
| 1 | **Mind + loop** | `Relay.Core/Mind` (observations, moves, schema, prompt, `ModelMind`, `ScriptedMind`, `TaskLoop`), `Relay.Core/Decisions` (`Decider`, `decisions.json`, usage lines), SSE streaming in the gateway, conversation-mode retention settings; wired behind `orchestrator.mode = "mind"` beside the old pipeline so Ministral is evaluated on the new contract before anything is removed |
| 2 | Centralize the loop | `SessionCoordinator.Tasks` → `TaskEngine` built on `TaskLoop`; state machine collapse; recall context and fast-path registry |
| 3 | Grammar removal | delete the grammar, judges and classifier; migrate tests to `ScriptedMind` |
| 4 | Feed UI | one feed, one input, drawers |
| 5 | Delegation | `DelegateRuntime` with mind-authored prompts, partial observations, `digest.md`, bounded multi-turn |
| 6 | **Capability building** | `Relay.Core/Tools` (`ToolPackage`, `ToolStore`, `HostFunctions`, `ToolRunner`, `ToolBuilder`, `ToolRuntime`, `BuildPrompt`), the Jint `tool` task in `Relay.Worker`, `add_tool` as a Tier B action, the build lifecycle in the coordinator, world-clock scenario end to end |
| 7 | Evaluation and tuning | `move` expectations in the harness, live runs, first weight tuning from usage lines |

### Slice 1 live results (Ministral 8B Q4_K_M, the mind set of six cases)

The set (`tests/Relay.Tests/Evaluation/mind/mind-moves.json`) is scored on moves, first read, route and outcome without executing anything; `tools\live-eval.ps1 -Filter FullyQualifiedName~EvaluationTests.LiveModelMind` runs it. Six runs took the model from 2/6 to 6/6; every change was to the contract, the prompt or deterministic code, and each is general rather than case-shaped:

1. **2/6.** Searched notes for the time in Tokyo three times; delegated nothing; filtered a search to a project the user had not named.
2. **0/6.** A longer prompt fell over Ollama's default 2048-token context: it drops the middle of the prompt (the constitution) without any error, and the model asked the user about everything. The runner now derives a model with `num_ctx` set; the README states the requirement.
3. **4/6** with 8k context. The repeat guard (the loop refuses an identical tool call and tells the mind what it returned the first time), the step budget in the transcript header ("step 2 of 3", "this is the last step"), `needs` and `complexity` anchors, and the rule that a project named in the request is read with a tool first.
4. **5/6.** The mind converted the UTC time in the date line to Tokyo time itself — wrongly by an hour in one run, correctly in the next. The mind now has no clock: the transcript carries the date only, and "you know facts, not the present" is in the constitution. It built `world_clock` in the next run.
5. **6/6.** A `search` filtered to a project that finds nothing now widens to every project and says so in its summary, so the mind does not conclude "no decision" from the wrong drawer. Also from these runs: the delegate token budget has a floor of 800 (the model wrote 200), and `say` with `done=false` is described as a step that does nothing.

What the runs also showed and what waits for slices 5–7: the first read's `needs` is loose (a research ask was labelled `new_tool`), so the route hint is sometimes wrong and the mind ignores it — the usage lines are the material for tuning those weights; the delegate prompt the mind writes is short and needs the `digest.md` and prompt-quality work of slice 5.

### Slice 6: what landed and how the loop sees a build

A build is a pending operation of the task, like a delegate call, and the loop observes every stage of it as its own step: `build started` (the mind is told to wait or stop), `drafted` (what the package looks like: arguments, source size, host functions, tests queued), `tested` or `failed` with the sandbox's verdict (a failed attempt says a retry follows; the second failure ends the build with the draft left in staging and the mind told to answer what it can), `stopped` when the mind's `stop` cancels it. A tested draft becomes one `add_tool` proposal that the user approves inline; the loop observes the approval and the execution, then `promoted` with the tool's usage, and the same task calls the tool and answers. `use_tool` distinguishes built-in tools (in-process, as before) from promoted ones (one sandboxed worker run, recorded as `tool.ran` with the run id, elapsed time and result size). Every stage is a ledger event (`tool.build.*`, `tool.promoted`, `tool.ran`); the package source is never in the ledger, only its hash.

The sandbox was tested with hostile scripts before the mind was allowed near it: undeclared host functions are denied at the broker and surface as a catchable error in the script; `require`, `process`, `fetch`, `XMLHttpRequest`, `WebAssembly` and `import()` do not exist; `relay` is frozen; endless loops hit the statement cap, deep recursion the recursion limit, allocation the memory limit, a slow script the wall clock; more than 50 host calls in one run, or a result over 20 000 characters, fails the call. Package validation refuses bad names, missing descriptions, undeclared argument names in tests, unknown host functions, sources without `run`, and packages without tests. Promotion refuses an untested draft, a draft whose source changed after testing, and a name that already exists; reverting the change set removes the tool from the mind's list.

The evaluation set gained `unseen:mind/world-clock-built`: the same Tokyo question once the tool exists, scored on `use_tool:world_clock` being the first move (not a second build, not a guess, not a delegation). A mind case may now declare `tools` — built tools with a scripted result — so the harness can put the mind in the state a promotion leaves behind. In mind mode without a worker host (no sandbox) the build move is absent from the contract and the mind is told so.

### Slice 6 live results (Ministral 8B in both seats: the mind and the drafter)

Two live tests carry the acceptance: `ToolBuildingTests.LiveModelDraftsAWorldClockThatPassesItsOwnTestsInTheSandbox` (the drafter alone) and `MindModeTests.LiveModelMindBuildsTheWorldClockPromotesItAndAnswersWithIt` (the whole loop: ask → build → draft → sandbox tests → one approval → promote → use → answer). Both pass, and the mind set stays at 6/6 with the new build text. What it took, each a general fix rather than a case-shaped one:

- **Tests must pin the clock.** A drafter's test that only checks the shape of the result passes a tool that returns the wrong time. Build tests now run with the clock pinned at a fixed instant and the build prompt states what every host function returns at that instant (computed by the same code that answers the tool), so the drafter writes `contains: "21:00"` for Tokyo and a source that returns UTC fails its own test.
- **Test results are for the drafter only.** The first version quoted the failing result to the mind too; the mind read "21:00" out of a failure detail and answered with it — a value from the pinned instant, not the present. The mind and the ledger now see which tests failed and why, never what the tool returned; the drafter alone sees results, labelled with the pinned instant.
- **The failure is quoted at the line.** A retry gets the failing source line with context and, when the source threw its own error, is told so; a missing result key that exists one level down is named with the fix ("build the result object yourself"). The drafter's second attempt now usually passes where it used to repeat the mistake.
- **One worked example beats three rules.** The drafter used `relay.now()` (UTC) for a local time until the build prompt carried a six-line local-time tool as an example, with the sentence that the local time anywhere is only ever `relay.zone(...).local.*`. `relay.time.zone` and `relay.zone` both work in the sandbox, because the drafter writes either.
- **Examples in the mind's contract are copied.** A literal `world_clock` example in the build move's description made the mind call a `world_clock` tool that did not exist and build `world_clock` for a research question. The description now states the principle (name the tool for what it does, make what varies an input; research is a delegate, never a build) and names nothing.
- **Names are normalised, misses are corrected.** The mind once called `time_tokyo(zone)` — the listing copied as a name — and, told no such tool existed, estimated the time. A tool name is now read as the identifier before any parenthesis, a miss names the nearest tool, and a proposal shaped like a tool (inputs, outputs, a `new_tool` need) is answered with "a tool is built, not proposed". The constitution adds that an answer about the present repeats a tool result from this task or says the result could not be had.

Still open from these runs: the mind names Tokyo-specific tools (`time_tokyo`, inputs "none") even though the drafter then makes them general (a `zone` argument); the drafter cannot rename, so the promoted tool carries the mind's name. Letting the drafter propose the name — with the mind learning it from the `promoted` observation — is the natural fix and waits for the next pass over building.

## Decisions taken with the user

JavaScript (Jint) for tools · delegation raised as a proposal after each failed attempt with retry · typing-first input, Flow as one typist · OpenAI-compatible delegates only, bounded multi-turn in the first cut · one approval at promote · the mind names note type and project, filing is an approval under the confidence threshold · hard rules loosened behind the `development` switch in `decisions.json` while the architecture is built (structural sandbox rules are not loosened; there is nothing to flip).
