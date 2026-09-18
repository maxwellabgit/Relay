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
| Branch | `main` (landed from `refactor/jev-decision-engine`) |
| Spec | `docs/JEV_REFACTOR.md` + plan `RELAY_Jev_Refactor_Plan.md` |
| Phase | **11 production path cut over; capability dispatch wired; live runners honest** |
| Alpha complete | **Not claimed** — DoD §14 still requires Windows live gates with real WinUI + Wispr + Jev + local generation + restart/replay on a host that has secrets. Code build + SKIPPED live scaffolding is done. |

Prior branch `origin/refactor/jev-runtime` is **reference-only** (supersede decision).

## Phase 0 baseline characterization (this Windows host)

Host: Windows 11, SDK `10.0.400` at `%LOCALAPPDATA%\Microsoft\dotnet` (not on default PATH). Desktop **does** compile here.

| Command | Result | Evidence |
| --- | --- | --- |
| `dotnet build Relay.slnx -c Debug` | Succeeded — 0 errors, 15 xUnit analyzer warnings | `docs/baseline/build.txt` |
| `dotnet test` → `Relay.Core.Tests` | **33/33 passed** | `docs/baseline/test.txt` |
| `dotnet test` → `Relay.Tests` | **396 passed / 2 failed / 398 total** | `docs/baseline/test.txt` |
| DevHarness `slice1`–`slice7` | **All exit 0**, isolated `.dev-runs` artifacts | `docs/baseline/harness.txt` |

### Known failures at baseline (legacy `SessionCoordinator` path)

Do not treat these as Phase 0 regressions. They live in code retained for `Relay.Tests` only.

1. `Relay.Tests.RecoveryAndFailureTests.LedgerWriteFailureLocksAndPreservesTheDraft` — expected `Locked`, got `Ready` (`RecoveryAndFailureTests.cs` ~177).
2. `Relay.Tests.RecoveryAndFailureTests.CrashDuringCaptureIsDetectedAndTheDraftIsRecoverable` — no `StateChanged` to `ORGANIZING` (`RecoveryAndFailureTests.cs` ~44).

## Current tree (alpha code build)

| Area | State | Evidence |
| --- | --- | --- |
| Product / architecture docs | Rewritten for decision engine + Jev + v0.1 scope | `PRODUCT.md`, `ARCHITECTURE.md`, `README.md`, `JEV_REFACTOR.md` |
| `CaseRuntime` + capability dispatch | Raised children with v0.1 `AllowedCapabilities` invoke registered handlers | `CapabilityDispatchTests` |
| Production Desktop composition | `CaseRelayHost` → registry + generator + lifecycle → `CaseRuntime` + surface | Desktop Debug build |
| Hosted toggle (independent of listening) | Surface `ToggleHostedJudgments`; Desktop chip tap | `CaseRuntimeSurface` |
| Transcript ingest | Surface `IngestTranscript` → `StreamIntake` while listening | `IRelaySurface` |
| Jev / judgment contracts / TypeSafe client | Contracts + fake + HTTP client + persistence/cache/lifecycle | Core + Gateway tests |
| Decision engine | Engine + policy + mind adapter; multi-raise preserves `capabilityId` | `DecisionEngineRuntimeTests` |
| Four v0.1 capabilities | acronym + note + task + direct.answer | capability unit tests |
| Improvement `PatternSignature` / evaluator | draft→eval→approve→shadow→active→revert | `ImprovementLifecycleTests` |
| DevHarness `jev-*` | scripted / outage / privacy / improvement; `jev-live` calls TypeSafe or `SKIPPED` | `JevScenarios.cs` |
| Windows live gate runner | `dev/run-live-gates.ps1` — honest SKIPPED preflight | exit 3 when env incomplete |

## Environment note

This verification host is **Windows**. Live Jev and local-generator gates still require `TYPESAFE_API_KEY` / `RELAY_MODEL_ENDPOINT` (and Wispr for listening proof). Until those pass on a real Desktop session, alpha remains **not complete**.

Open questions: repo-root `QUESTIONS.md`.
