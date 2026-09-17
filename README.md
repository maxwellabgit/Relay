# Relay

Relay is a **local-first desktop assistant that accumulates personal capabilities under a loop that does not act without recorded permission**.

It understands enabled conversations, direct requests, and connected workstreams; retrieves relevant context; performs useful work; and turns recurring friction into reusable tools, workflows, and preferences. The user should need to repeat fewer instructions, prepare fewer prompts, and manually coordinate fewer steps over time.

**RELAY0** is the whole system: a small, replaceable local orchestration model, personal memory, an asynchronous task runtime, approved tools and workflows, and optional external AI workers. Initial personalization lives in memory, preferences, tool code, and workflow definitions. Those assets must survive replacing the local model. Training the model’s weights from collected examples is a later possibility, not part of the essential build.

This README is the essential target. It is not a status report.

**Authoritative docs (vNext):** [`docs/PRODUCT.md`](docs/PRODUCT.md) · [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) · [`docs/STATUS.md`](docs/STATUS.md). Historical documents live under [`docs/archive/`](docs/archive/). local testing ground: [`dev/`](dev/).

Windows 11 is the first surface. The architecture is not Windows-specific.

## Observe, decide, act, resume

The runtime collects input and delivers it when a decision is needed. Conversation is one origin among equals: the runtime buffers timestamped transcript segments and submits windows on a configured cadence. Window length and evaluation frequency are separate settings. Original passages remain retrievable beyond the current prompt.

The local model interprets meaning: what matters, what the user is trying to accomplish, what information is missing, and which action would help. A window can produce a note, updates to existing tasks, several new task proposals, or no action.

The same action vocabulary applies to direct requests, observed conversation, and dialogue with Relay. Origin affects authorization, urgency, and presentation. It does not prevent Relay from proposing a useful capability.

Each decision is one schema-constrained step: a short read of the situation, one typed move, and a one-line feed sentence. Deterministic software owns scheduling, permissions, execution, task state, and the record of what actually happened. The model proposes; it does not grant permission. Authorization comes from the user’s instructions and recorded grants.

```
observation ──► local model (read + move + feed)
                 │
                 ├─ deterministic gates (policy, budgets, grants)
                 ├─ the move’s consequence (tool, workflow, package, sandbox, card…)
                 └─ what happened, recorded ──────────────────────────► next decision
                    (this task, or another that is ready)
```

## Asynchronous work

**Tasks wait independently. The local model serves whichever task needs a decision next. Observation continues while work is in flight.**

When research is delegated, Relay saves the task’s objective, current plan, selected context, permissions, and pending request identifier. The runtime awaits the external response while the local model handles other work. Listening and direct requests do not stop because a delegate is outstanding.

The essential build can run one local inference at a time while several tools or external requests are in progress. Each inference receives the state and context relevant to its task.

The runtime serializes changes to each task, records processed events, and checks request and task versions before resuming. Late results cannot silently revive cancelled work or execute an obsolete plan. Task state persists independently of a live model session.

Streaming text can update the feed without a model call for every fragment. Completion, failure, user correction, or a defined progress checkpoint can trigger reconsideration. Waiting alone triggers no inference.

## A shared continuation contract

Every resumed decision combines:

- Stable orchestration instructions and the available action definitions.
- The original request, current approved objective, relevant history, and completion criteria.
- The new result, its sources, unresolved questions, and remaining permissions and budget.

External replies are normalized by the runtime first (provider payload and errors). The model then interprets the content against the particular task. A convincing summary alone does not establish completion.

The review instruction is consistent: **identify what the evidence establishes, identify what remains unresolved, compare the result with the objective, and choose the next useful action**. Possible outcomes include answering, retrieving more context, asking a delegate to investigate further, requesting clarification, or proposing a task revision.

## Initial capabilities

| Capability | Purpose |
| --- | --- |
| Gather context | Search and read relevant notes, conversations, projects, task history, and tool and workflow descriptions. Preserve source references. |
| Keep and organize information | Write tentative scratchpad entries; create, connect, file, and revise notes and projects with history. |
| Use a tool | Run a registered function with validated inputs. |
| Run a workflow | Run a named, versioned sequence that composes retrieval, tools, delegation, and formatting; it can wait and resume through the task runtime. |
| Delegate | Assemble a contextual prompt, choose a model and capability profile, request research or specialist work, and continue the exchange when useful. A search prompt must be backed by an actual search integration. |
| Manage work | Create, resume, split, prioritize, defer, or cancel tasks; propose revisions when context changes the objective. |
| Communicate | Ask a necessary question, present an approval proposal, report progress, or deliver a checked result. |
| Improve | Propose a preference change, reusable tool, or workflow; build and evaluate it before activation. |

Intent, complexity, missing capabilities, and uncertainty are assessed while selecting an action. A simple lookup may need internet access; a difficult task may be answerable locally. Delegate profiles describe available capabilities as well as model choice.

## How Relay becomes personal

Task outcomes, repeated instructions, user corrections, and recurring sequences of work provide evidence of an improvement opportunity. Relay periodically reviews that evidence and can also act on an explicit request for an upgrade.

1. **Identify the friction.** Point to concrete examples and estimate what a change would save or improve.
2. **Specify the change.** Define when it applies, its inputs and outputs, required access, expected behavior, and how to judge success.
3. **Build and evaluate.** Create the tool or workflow in isolation and exercise it against representative examples, including failures. Use existing tools when they already meet the need. Simple workflow definitions are enough at first: named, versioned sequences that compose existing capabilities (context retrieval, tool calls, delegation, formatting) and can wait and resume.
4. **Approve and activate.** Present the working change, evaluation results, and requested permissions. Install an approved version in that user’s workspace.
5. **Observe actual use.** Compare outcomes with the previous process. Keep useful changes, revise weak ones, and support disabling or reverting them.

A shared codebase and replaceable model can therefore support different capabilities for different users. Improvements remain versioned, inspectable, and removable. Record usage examples, corrections, and results now.

## Guiding heuristics

1. **Treat Relay as a personal workflow compiler.** Demonstrated, repeatable operations become reusable tools and workflow definitions. Context-dependent judgments remain model decisions.

2. **Treat the model as a shared decision-maker with separate case files.** A task’s continuity lives in its recorded objective, evidence, decisions, and pending work. Suspending one task leaves the model available for another. Observation of enabled streams does not wait on any one task.

3. **Treat personalization as accumulated capabilities.** Memory, preferences, tools, and workflows belong to the user. A model upgrade is evaluated against those capabilities and must preserve them.

4. **Treat improvement as a measured hypothesis.** Generated code or a workflow is valuable when it reduces effort or improves outcomes in actual use. Track successful completion, user corrections, manual interventions, repeated use, and time saved; proposal count is not the goal.

5. **Spend attention deliberately.** Preserve useful context quietly. Prioritize direct requests and time-sensitive findings while allowing background work to progress. An observed possibility does not automatically deserve an interruption.

6. **Use uncertainty to select the next step.** Missing evidence can call for retrieval, an experiment, or clarification. Confidence in an interpretation, an action, and a factual claim are different judgments; none grants permission.

7. **Preserve intent while adapting the approach.** Relay may change its plan within the approved objective and permissions. A material change to the objective, deliverable, or scope is presented with evidence for approval, and recorded as a task revision without erasing the original request.

## What the essential build must prove

These scenarios run through the desktop with the real local model.

- A messy conversation window produces a useful, source-linked note or task, and later corrections update the same work.
- A research task retrieves relevant personal context, delegates through working search tools, and returns an evidence-backed answer while direct interaction and observation continue.
- Repeated workflow friction leads to a tested, approved personal tool or workflow that is successfully reused and can be reverted.
- A task can wait, fail, resume, or be cancelled without losing its objective, duplicating side effects, or blocking unrelated work.

The first build needs one working local model profile, one research delegate with real search when search is claimed, a small useful tool set, persistent task and source storage, an isolated tool- and workflow-building path, a true multi-task queue (one local inference at a time, many waits in flight), and one feed and composer for results, instructions, and approvals.

The durable base that this target sits on — append-only ledger, policy and capabilities, sandboxed execution, source-linked notes and projects, evaluation harness — is specified in `docs/`. The loop contract (observation types, moves, feed) is in `docs/09-orchestrator-rebuild.md`; the distance between this target and the tree, and the sequence that closes it, is in `docs/10-alpha.md`.
