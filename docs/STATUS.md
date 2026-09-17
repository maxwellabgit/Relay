# STATUS.md

What has been verified, and how. Scripted evidence is never summarized as live evidence.

## Verification levels

1. **Deterministic unit** — ledger, schemas, policy, path guards, idempotency, recovery, stores, executors.
2. **Runtime scenario** — scripted model moves exercising complete case behavior.
3. **Replay** — recorded real model request/response pairs rerun against later runtime or prompt versions.
4. **Live Windows gate** — real WinUI, real local model, real Jint worker, real search adapter, Wispr Flow where applicable.

Live runners must fail preflight or report `SKIPPED: missing local model`. They must not return as passing when the environment is absent.

## Current tree (vNext branch)

| Area | State | Evidence |
| --- | --- | --- |
| Product contract (`PRODUCT.md`) | Written | Doc review |
| Architecture contract (`ARCHITECTURE.md`) | Written | Doc review |
| Retention ledger (`RETENTION.md`) | Written | Doc review |
| Historical docs archived | Done | `docs/archive/` |
| Legacy prototype tag | `legacy-prototype-e7e9421` | Local git tag |
| Characterization: ledger / PathGuard / AtomicFile / Ulid | Done | `tests/Relay.Core.Tests` deterministic |
| `CaseRuntime` + `OperationEnvelope` | Slice 1–2 | `Slice1RecoveryTests`, `Slice2AtlasRecallTests`, `Relay.DevHarness` |
| Persistent ready queue | Slice 1 | SQLite `ready_queue` via `ReadyQueue` |
| SQLite projections | Slice 1–2 | `ProjectionDatabase` + feed items per step |
| Local harness (`dev/`) | Present | `dev/*.ps1` + `src/Relay.DevHarness` (`--scenario slice1|slice2|slice3|slice4`) |
| Direct vertical path (Slice 2) | Done | Atlas beta recall + citations; propose edit/reject |
| Listening adapter (Slice 3) | Done | `StreamIntake` + observed case; `Slice3ListeningTests` |
| Research path (Slice 4) | Done | `ResearchBroker` + Lightshift cited path; `Slice4ResearchTests` |
| Tool/workflow generalization (Slice 5) | Not yet | — |
| UI projection coupling (Slice 6) | Not yet | — |
| Measured personalization (Slice 7) | Not yet | — |

## Legacy prototype (tagged `legacy-prototype-e7e9421`)

The prior tree at `e7e9421` retains useful primitives (ledger, PathGuard, policy/proposals, Windows integration, Jint worker, change sets, project/note stores) but still carries:

- Two model-driving loops (`TaskLoop` + `ObservingLoop`)
- Partial task durability
- No universal operation envelope
- Non-persistent scheduler (`TaskEngine`)
- Incomplete search↔delegate binding
- Prototype tool naming / structural-only workflow tests
- Heuristic-heavy memory
- Live tests that can pass without a model
- Documentation that over-claims completeness

Those components remain in the tree until vNext replacements pass equivalent characterization. They are not the authoritative product contract.

## Environment note

This agent’s verification host is Linux. `Relay.Core`, `Relay.Gateway`, and `Relay.Worker` are buildable here. `Relay.Desktop` (WinUI) and `net10.0-windows` projects require a Windows machine for compile and live gates.
