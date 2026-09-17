# RETENTION.md

Retention ledger for the vNext rebuild. Each legacy component is **retained**, **adapted**, or **replaced**. Do not delete replaced code until the replacement passes equivalent characterization tests.

| Existing code | Direction | Approximate reuse | Notes |
| --- | --- | --- | --- |
| `Ledger/*`, `AtomicFile`, `Ulid`, `IClock` | Retain | 80% | Characterization tests required before any format change |
| `PathGuard`, workspace roots, project/note/version stores | Retain and adapt | 60–75% | Keep path invariants; adapt callers to operation envelopes |
| `Proposal`, `PolicyEngine`, `CapabilityIssuer`, typed executor | Adapt around operation envelope | 40–60% | Envelope becomes the approval-bound authority object |
| DPAPI, ACLs, hotkeys, single instance | Retain | 70–90% | Windows-only |
| Jint worker, package validation, broker, job object | Retain and expand capability catalog | 60–75% | Keep sandbox; change build contract in Slice 5 |
| Gateway HTTP clients | Retain transport; replace research orchestration | 40–60% | Search + fetch adapters become deterministic |
| Change sets and reversible promotion | Retain | 60–70% | |
| WinUI styles and useful controls | Retain styling; replace orchestration-bound view logic | 20–35% | Slice 6: UI reads projections |
| `SearchIndex` | Keep as fallback; replace primary projection | 10–20% | SQLite FTS5 becomes primary |
| `TaskLoop`, `ObservingLoop`, `TaskEngine` | Replace | 0–15% | One `CaseRuntime` + persistent ready queue |
| `SessionCoordinator*` | Decompose and replace | 0–10% | |
| Existing end-to-end scenario tests | Retain requirements; rewrite against new runtime | 15–25% | |
| Historical docs (`docs/archive/*`) | Archive | Minimal | Not current specification |

## Deletion rule

A replaced component may be removed only when:

1. The replacement implements the same product invariant, and
2. Characterization or scenario tests that previously protected the invariant pass against the replacement, and
3. `STATUS.md` records the evidence level (deterministic / scenario / replay / live).
