# 10 · The Alpha

The README is the essential target. This document is the distance between that target and the tree, and the sequence that closes it. Alpha is reached when the README's four proof scenarios run against a real local model and a person can drive all four from the window.

The rule of the previous passes holds: each step lands with its tests before the next begins, the ledger and the state machine are changed by addition, and nothing is deleted until what replaces it passes the same scenarios.

## What is already built

The durable half. The append-only hash-chained ledger with verify, repair and `LOCKED` on tamper. `PathGuard` and the data root. Projects, notes with exact spans, versioned writes, archive and backup. The policy tier table with hash-bound approvals, single-use time-limited capabilities, and the typed executor with journal recovery. The worker sandbox inside a job object with the broker as its only file access. The Jint tool builder — one-file packages, pinned source hashes, sandbox tests with a pinned clock, one `add_tool` approval at promote, revert as a change set. The delegate runtime with mind-authored prompts, streaming partials, `digest.md`, and a bounded conversation under one approval. Change sets with a stored *before*. The mind, its move contract, and the self-observing `TaskLoop`, tuned live to 6/6 on the mind set and carrying the delegation and build acceptances. 675 tests.

## The gaps

### G1 · Two pipelines, and the local model still has two contracts

*Closed. All six commits of Step 1 have landed. `mind` is the default and the mind reads the stream itself; `IJudge` and both its implementations are gone; the mind produces the consistency verdict and the citations that only a plan carried; the grammar, the model orchestrator, `IOrchestrator` and `CompositeOrchestrator` are deleted; and the session machine is now only `Starting` / `Ready` / `Failed` / `Locked`. Capture and listening are surface flags (`CapturePhase`); turn semantics live on each task. A task that fails leaves the session Ready — only an organize or app incident takes the session to Failed. Direct asks that turn into research, organize, or improve work are relabelled on the record when the mind proposes the corresponding action.*

`orchestrator.mode` defaulted to `rules+model` and the mind loop was opt-in. Conversation windows were read by `IJudge` — a separate model role with its own contract and its own prompt — which created observed tasks that the mind then planned. The README commits to one interpreter and one action vocabulary for every origin. While two pipelines stand, every capability below has to be built twice, and the live tuning the mind has had does not govern what happens when Relay is listening.

Gone from the tree: `RuleBasedOrchestrator` and its three partials, `CompositeOrchestrator`, `ModelOrchestrator`, `IOrchestrator`, `TurnRequest`, `HeuristicJudge`, `ModelJudge` and the direct classifier. Still present and still weighting, because nothing yet replaces them: the `NoteExtractor` cue regexes and the `NoteRouter` scoring.

### G2 · Nothing picks the task that needs a decision next

Many tasks can be live and waiting at once; that part is real. But stepping is driven entirely by whoever calls into the coordinator — an ask, an approval, an external completion — and there is no ready queue, no priority, and no gate on the local model. A judge pass, two mind loops and a reply digest can hit the same endpoint at the same moment. On a local 8B that is contention and timeouts, and it contradicts *one local inference at a time while several tools or external requests are in progress*.

The single session-wide `RelayState` also drives the foreground task and rejects new captures for the length of a turn.

### G3 · Task state does not survive a restart in a resumable form

`tasks\{id}.live.json` carries the stage, the proposal ids and the *character count* of the instruction. Not the objective, not the plan, not the waits, not the pending external request id, not the permissions. At start Relay detects interrupted work, writes a ledger event and a Review item, and resumes nothing. Delegate conversations live in memory only.

### G4 · No idempotency or versioning on resume

There is no task version and no record of which events a task has already applied. `CompletePendingOperation` does not check a cancelled task. That late results cannot revive cancelled work is presently true only when the task has already left memory.

### G5 · Search is nominal

*Closed with Step 3. One search provider behind the brokered `web_search` tool, called only through Relay.Gateway under standing grant or per-task approval; hits are stored as citable local artifacts. `supportsSearch` means the profile has a search integration (and search is configured); profiles without it keep the allowSearch=false correction.*

~~`allowSearch` adds one sentence to the delegate's prompt and a flag to the ledger; `supportsSearch` is a claim in configuration. There is no search integration in the solution — no provider client, no MCP, no browser. Whether anything is searched depends on the remote endpoint alone. The README's own rule, that a search prompt must be backed by an actual search integration, is violated today.~~

### G6 · Workflows do not exist

No type, no store, no move, no action, no directory. The capability table and the improvement loop both name them.

### G7 · Improvement only happens inside a task

`UsageRecorder` writes a line per task; nothing in `src\` reads one. *Relay periodically reviews that evidence* is unimplemented, so friction becomes a proposal only when the user asks or the mind decides mid-task.

### G8 · Named in the target, absent in the tree

No `split`, `prioritize`, `defer` or revise-objective (cancel and stop exist). No scratchpad. No memory distinct from notes, which thins the portability claim — personalization today is notes, preferences and tools. The mind cannot search its own task history or read a workflow description. The window is seven panels, not one feed and one composer.

| Gap | Blocks |
| --- | --- |
| G1 two pipelines | scenario 1 as *the mind* interpreting; doubles every step below |
| G2 no scheduler | scenario 4; the asynchronous half of scenario 2 |
| G3 no durable task | scenario 4 |
| G4 no versioning | scenario 4 |
| G5 nominal search | scenario 2 |
| G6 no workflows | the workflow half of scenario 3 |
| G7 no friction review | scenario 3's *leads to* |
| G8 the rest | nothing; post-Alpha |

## The steps

### Step 1 — One pipeline

`mind` becomes the default. The mind reads conversation windows itself: a window closes into an observation and the loop decides, with the same moves and the same action vocabulary it has for a typed instruction. The grammar, both judges, the model orchestrator and the classifier are deleted and their scenarios re-expressed over `ScriptedMind`. `RelayState` collapses to `Starting`, `Ready`, `Failed`, `Locked`, with listening and capture as flags of the surface rather than states of the machine; per-task status carries what the old turn states carried.

Lands in six commits, each green: the mind over the stream (judge still in tree, unused); the default flip; the judge deleted and its scenarios re-expressed over the mind; the two things only a plan produced carried onto the mind's path; the grammar and the model orchestrator deleted; the state collapse.

The fourth commit is what the deletion was waiting for. A finished plan carried two things the mind's loop did not: `Consistent`, the verdict of a check, and `Citations`, what the answer stands on. Both are load-bearing — the verdict is what makes the arbiter raise an alert and what titles the card *Conflict*, and the citations are how the user sees what an answer was built from — and neither could be recovered from a mind that only said sentences. So the mind produces them now. The verdict is one optional word on the read, `consistent` or `conflicts`, in the constrained grammar so a model cannot invent a third; it is set on the step the mind reaches it, which is before it proposes anything, because the card that asks for approval has to be able to say what it is about. The citations are derived rather than declared: they are what the mind *opened by id* — `read_note`, `read_excerpt`, `read_artifact` — in the order it opened them. A search returns candidates, which is not grounding; opening one of them is.

Two things the judge's deletion hands to the gate. The authored evaluation sets no longer score listening at all: the judge stage had its own cases and its own expectations, and the mind's stage scores a task loop, not a pass over a window. An observing stage — one pass against a recorded host, scored on what it raised — is the gate's to add, and until it exists listening is held only by the scenario tests. And `tools\ui-smoke.ps1` and `tools\acceptance-run.ps1` can no longer exercise listening without a model, because there is no heuristic reader behind the chord any more; both are rewritten at the gate against a local mind.

The fifth commit is the deletion, and what it costs is the evaluation set. There was a plan stage with twenty-four cases of real requirement in it, written against a grammar that no longer exists. They were not thrown away. Two of the expectation's members survived by being re-pointed at the mind's propose moves: `targets`, which says what a proposal was *about* — `create_project.name=Garden` — and `contract`, which says a self-change carried its four fields. A move says what was proposed; a target says what it was proposed about, and that is exactly where a plausible-looking proposal goes wrong. What was deleted instead was the members a mind cannot produce: `understood`, because a model always answers, replaced by `completes`, which asks the loop to reach an end or a wait; `actions` and `forbiddenActions`, subsumed by `moves` and `forbiddenMoves`; and `knowledgeGap`/`capabilityGap`, which collapse to `needs: ["new_tool"]` now that the mind's knowledge state is derived from its own read rather than declared beside it.

Nothing in the evaluation is scored by a grammar any more, so the sets are no longer split by *what* scores them. They are split by the world a case needs: the top-level sets run against Atlas and Home, `mind/` adds the tools the mind built for itself, and `model/` adds Lightshift and a configured external profile. A test world is now built through the same calls the Projects and Memory panels make, never by asking the mind to interpret a sentence — that keeps the setup of a test out of what the test is about, and it is why the deletion of the phrase grammar did not take the scenarios with it.

Running the suite found one thing the deletion had quietly broken, and it is the kind of hole worth naming. The improvement contract is only owed *in an improve task*, and it was the grammar that classified "keep responses concise" as one. With the grammar gone every direct ask starts as something to answer, so nothing ever reached the lane where the contract is enforced: Relay could have changed its own preferences without stating a benefit or an acceptance criterion. The lane is not cosmetic — policy asks what kind of task it is deciding in — so it cannot be left to phrasing. A task is now an improvement the moment Relay proposes changing itself, whatever words started it, and the relabel is on the record with what the lane was before.

Migrating the tests found a second hole of the same family, and a worse one. `config\prompts\mind.md` was both the file an approved `update_prompt` writes and the file the mind's constitution is read from, and a constitution read from a file *replaces* the built-in one rather than adding to it. So approving a bounded one-line instruction — "never use bullet lists" — would have silently discarded the whole governing prompt: never invent ids, never act directly, read the transcript before every step, the JSON contract itself. The proposal's own stated scope would have been a lie, and the thing the scope is checked against would have been the thing it destroyed. The constitution now has its own name, `config\prompts\constitution.md`, which is deliberately not one of the names a proposal may write; `mind.md` stays the approved fragment and is appended, exactly as the build and digest prompts already worked. Policy's list of writable fragment names and the runtime's are now one list, so neither can drift into permitting what the other refuses, and the dead `planner` fragment is gone from both.

*Proves:* a messy conversation window produces a useful, source-linked note or task through the loop that has been tuned, and every step below is written once.

### Step 2 — The runtime the README claims

*Closed.* A `TaskEngine` over `TaskLoop` with a ready queue that selects the task whose next decision is due, one semaphore so exactly one local inference runs at a time while any number of tools and external requests are in flight, durable task records holding objective, plan, waits, pending request id and permissions, resume on start for approval and user waits, and a task version plus an applied-event set so a late reply cannot touch cancelled or advanced work. Cancellation becomes final: a cancelled task refuses completions rather than observing them. Local inference only is gated (`StepAsync` and digest); tools, workers, external HTTP and approvals stay concurrent.

*Proves:* a task can wait, fail, resume or be cancelled without losing its objective, duplicating side effects or blocking unrelated work — and observation continues throughout.

### Step 3 — Real search

*Closed.* One provider behind one brokered tool (`web_search`), called through `Relay.Gateway` (still the only thing that opens a connection), under a standing grant (`sources.allowOnlineSearch`) or a per-task approval (approving a `model.request` with `allowSearch`), with results stored as citable local artifacts under `artifacts/search`. `supportsSearch` means the profile has a search integration and Relay's search client is configured; a profile without one keeps the correction the loop already makes. Citations still require the mind to `read_artifact`.

*Proves:* a research task retrieves personal context, delegates through working search, and returns an evidence-backed answer while interaction and observation continue.

### Step 4 — Workflows, minimally

A named, versioned definition that composes context retrieval, tool calls, delegation and formatting, run through the same loop so its waits and resumes are the task runtime's. Built and evaluated in isolation like a tool, promoted by one approval as a change set, revertible, and listed to the mind beside the tools.

*Proves:* the workflow half of scenario 3, and gives the improvement loop something other than a tool to propose.

### Step 5 — One feed, one composer

The panels collapse into a chronological feed of the mind's own sentences, with approvals inline where they occur and evidence expandable beneath them, one composer that takes instructions and answers, and projects, review, tasks and the ledger as drawers. One status line.

*Proves:* all four scenarios are drivable by a person, which is what Alpha means.

### Step 6 — Friction review

On idle or at session end, the usage lines are read and a repeated friction becomes one proposal — a tool, a workflow, or a preference — carrying the examples that motivated it, evaluated before activation and revertible after.

*Proves:* repeated friction *leads to* an improvement without the user having to ask for it.

## The Alpha gate

The four scenarios of the README as live tests against a real local model, each also runnable by hand:

1. A messy conversation window produces a useful, source-linked note or task, and a later correction updates the same work.
2. A research task retrieves relevant personal context, delegates through working search, and returns an evidence-backed answer while direct interaction and observation continue.
3. Repeated workflow friction leads to a tested, approved personal tool or workflow that is reused and can be reverted.
4. A task waits, fails, resumes and is cancelled without losing its objective, duplicating side effects, or blocking unrelated work.

Plus: `tools\ui-smoke.ps1` driving all four through the feed against a throwaway data root, and the ledger checks that hold today — no overheard words, no tool source, every model round trip sized and recorded.

## Held for after Alpha

`split`, `prioritize`, `defer` and revise-objective as explicit verbs. The scratchpad. A memory store separate from notes. A task-history tool. Connected workstreams as a third origin. Weight tuning from usage lines (slice 7 of docs/09). Packaging and a week against real Wispr Flow. None of them blocks a proof scenario; the README keeps them because it is the target, not a status report.
