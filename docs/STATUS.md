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
| Tag | `pre-jev-refactor-55551224` |
| Branch | `refactor/jev-decision-engine` |
| Spec | `docs/JEV_REFACTOR.md` + plan `RELAY_Jev_Refactor_Plan.md` |
| Phase | **5 hosted grants landed; Phase 6 decision engine next** |
| Alpha complete | **Not claimed** |

Prior branch `origin/refactor/jev-runtime` is **reference-only** (supersede decision). This branch re-implements against `docs/JEV_REFACTOR.md` / the plan contracts.

## Phase 0 baseline characterization (this Windows host)

Host: Windows 11, SDK `10.0.400` at `%LOCALAPPDATA%\Microsoft\dotnet` (not on default PATH). Desktop **does** compile here.

| Command | Result | Evidence |
| --- | --- | --- |
| `dotnet build Relay.slnx -c Debug` | Succeeded — 0 errors, 15 xUnit analyzer warnings | `docs/baseline/build.txt` |
| `dotnet test` → `Relay.Core.Tests` | **33/33 passed** | `docs/baseline/test.txt` |
| `dotnet test` → `Relay.Tests` | **396 passed / 2 failed / 398 total** | `docs/baseline/test.txt` |
| DevHarness `slice1`–`slice7` | **All exit 0**, isolated `.dev-runs` artifacts | `docs/baseline/harness.txt` |

### Known failures at baseline (legacy `SessionCoordinator` path)

Do not treat these as Phase 0 regressions. They live in code Phase 11 deletes.

1. `Relay.Tests.RecoveryAndFailureTests.LedgerWriteFailureLocksAndPreservesTheDraft` — expected `Locked`, got `Ready` (`RecoveryAndFailureTests.cs` ~177).
2. `Relay.Tests.RecoveryAndFailureTests.CrashDuringCaptureIsDetectedAndTheDraftIsRecoverable` — no `StateChanged` to `ORGANIZING` (`RecoveryAndFailureTests.cs` ~44).

### Phase 0 script fix

`dev/run-relay.ps1` now forwards `--scenario` to DevHarness (previously harness-only runs always defaulted to `slice1`).

## Current tree (pre–decision-engine cutover)

| Area | State | Evidence |
| --- | --- | --- |
| Product / architecture docs | Rewritten for decision engine + Jev + v0.1 scope | `PRODUCT.md`, `ARCHITECTURE.md`, `README.md`, `JEV_REFACTOR.md` |
| `CaseRuntime` + envelopes + projections | Present (Slices 1–7 scripted) | `Relay.Core.Tests` + DevHarness |
| Production Desktop composition | Still `SessionCoordinator` / `RelayRuntime` | `App.xaml.cs` |
| Jev / judgment contracts / TypeSafe client | Contracts + fake + HTTP client + persistence/cache/lifecycle | Core 49; Gateway 12 |
| Hosted grant / disclosure | Source classification + grants + DisclosurePolicy + surface commands | `HostedDisclosureTests` (10) |
| Decision engine (`ICaseDecisionEngine`) | **Absent** | — |
| Four v0.1 capability registry | **Absent** | — |
| Improvement `PatternSignature` / evaluator | **Absent** (friction still groups by kind alone) | `FrictionEvidenceStore.Suggest` |

## Historical slices (still valid as characterization)

Slices 1–7 on `CaseRuntime` with scripted minds remain the pre-Jev characterization suite. They are not the v0.1 production architecture. See archived slice table in git history of `ARCHITECTURE.md` and `docs/archive/`.

## Environment note

This verification host is **Windows**. `Relay.Desktop` (WinUI) and `net10.0-windows` test projects compile and run here. Cross-platform Core projects also build. Live Jev and local-generator gates still require secrets/endpoints and are not claimed.

Open questions: repo-root `QUESTIONS.md`.
