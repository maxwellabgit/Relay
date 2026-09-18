# STATUS.md

What has been verified, and how. Scripted evidence is never summarized as live evidence.

## Verification levels

1. **Deterministic unit** — ledger, schemas, policy, path guards, idempotency, recovery, stores, executors.
2. **Runtime scenario** — scripted minds / decision fixtures exercising complete case behavior.
3. **Replay** — recorded real provider request/response pairs rerun against later runtime versions.
4. **Live Windows gate** — real WinUI, real Jev (when granted), real local generator, Wispr Flow where applicable.

Live runners must fail preflight or report `SKIPPED: missing …`. They must not return as passing when the environment is absent.

## Refactor state

| Field | Value |
| --- | --- |
| Baseline | `55551224b7a7fb5006f752d1015a9937aeea4f10` |
| Checkpoint tag | `alpha-code-build-8b65b2d` (`8b65b2d`) |
| Branch | `refactor/production-alpha-finish` |
| Spec | `docs/JEV_REFACTOR.md` + plan `RELAY_Jev_Refactor_Plan.md` |
| Phase | **production composition + dogfood telemetry in; core path tests green; alpha not complete** |
| Alpha complete | **Not claimed** |

### Complete on this branch (evidence)

* Checkpoint tag + finish branch
* `RelayComposition` production root (shared stores, lifecycle-only Jev)
* Run-dir contract (`RELAY_RUN_ID` / `RELAY_RUN_DIR`) + `dev/run-relay.ps1` / `tail-relay.ps1` / `dogfood.ps1`
* Product telemetry + redactor + ProblemDetector + `latest-problems.md`
* Report problem UI + Ctrl+Shift+F12 + Cursor dogfood rule/hooks
* Direct input object persistence; observed child origin/span/BESS; task→approval; approve→execute; Jev retry pending; MaxSteps; project grant session ban
* **`ProductionCompositionTests` 11/11 passed** — `.dev-runs/production-alpha-finish-tests/step-23-42-core-path.json`

### Still incomplete for alpha

* **Self-improvement** — glossary harness still needs real correction evidence / shadow evaluation (plan §43)
* **Legacy cutover** — Session/Mind/Worker/obsolete paths still compiled (plan §44)
* **Settings schema reduction** (plan §45)
* **Stronger capability / privacy / outage / Windows live gates** (plan §46–49)
* **Windows live proof** — real Desktop + Jev + Wispr session with empty `latest-problems.md`

## Preserved strengths (do not regress)

Typed Jev contracts, TypeSafe parser/client, persisted judgment store/cache, grant model, capability registry, operation-envelope hashing, deterministic acronym candidate builder, surface abstraction, and honest `SKIPPED` live-gate behavior.

## Daily loop

```powershell
pwsh -File .\dev\dogfood.ps1
```

Step verification artifacts: `.dev-runs/production-alpha-finish-tests/`.

Open questions: repo-root `QUESTIONS.md`.
