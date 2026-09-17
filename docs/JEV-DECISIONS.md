# JEV-DECISIONS.md

Migration decisions made while implementing the Jev controller refactor.
Update this file whenever a default is chosen or an old test expectation is intentionally changed.

## Branch naming

- Spec requires `refactor/jev-runtime`. That exact name is used.
- Cloud agent branch prefix conventions are overridden by the explicit specification.

## SDK

- Pin `10.0.401` in `global.json` with `rollForward: disable` to match the Cloud Agent SDK used for baseline.

## Provider credentials

- `TYPESAFE_API_KEY` was **absent** at baseline. Live Jev transport tests will fail preflight until the key is provided.
- Fixture and replay clients require no key.

## Production vs historical mind

- `ICaseMind` / scripted minds remain for historical slice1–7 harness scenarios until replacement coverage lands.
- New production path uses `ICaseController` only.
- Harness will gain a composition factory; fixture provider mode is required for deterministic cloud gates.

## Cancellation semantics (intentional correction)

- Spec requires: never dispatch after cancellation; accept late results for audit without reopening the case.
- **§4 (2026-09-17):** `DispatchOperation` refuses not-yet-started ops on a cancelled case (status stays `cancelled`, zero executor calls). `AcceptOperationResult` / `CompleteOperation` may still record a late result without reviving the case.
- Slice 4 `Stale_completion_after_cancel_does_not_revive_case` updated: the former “stale execute after cancel completes the op” expectation is replaced by dispatch-refused + late `AcceptOperationResult`.

## Persistence / outbox (§4)

- Production stepping path: `ICaseController.Handle` → durable transition (events + commands) → dispatch **outside** the case lock.
- Historical slice1–7 minds run through `LegacyMindBridgeController` (deprecated bridge; scripted minds only — no live inference inside `Handle`).
- `objectiveRevision` is a separate field from case event `version`.
- Concurrency defaults (configurable via `RuntimeConcurrencyOptions`): local gen 1, Jev 4, search/fetch/delegate 4; case transitions serialized per case (runtime gate).
- Waiting cases skip controller/model work until a relevant wake/result/retry (`CaseInput`).

## Open questions for humans

1. Confirm `refactor/jev-runtime` branch name is acceptable for merge to `main`.
2. When will `TYPESAFE_API_KEY` be available in Cursor Cloud secrets?
3. Local model endpoint for live jobs (host/port/model id)?
4. Should WinUI Desktop binding wait for a Windows verification machine, or ship Core+harness first and stub Desktop composition?

## Decision log

| Date (UTC) | Decision | Rationale |
| --- | --- | --- |
| 2026-09-17 | Start at SHA `5555122`; no intervening diff | Matched inspected baseline |
| 2026-09-17 | Create branch `refactor/jev-runtime` | Spec §3 requirement |
| 2026-09-17 | Proceed fixture-only until TypeSafe key arrives | Key absent; live gates must fail preflight |
| 2026-09-17 | Keep historical `ICaseMind` tests until controller parity | Spec: retain temporarily for historical tests |
| 2026-09-17 | §4 outbox + `LegacyMindBridgeController`; cancel-before-dispatch refuses execute | Spec §4; Slice4 expectation updated |
| 2026-09-17 | §5 evidence + hosted grants; outbound policy before every hosted call | Spec §5; `hosted_eligible` ≠ permission |
| 2026-09-17 | §6 TypeSafe Jev transport + Fixture/Replay clients; Gateway.Tests | Spec §6; live requires `TYPESAFE_API_KEY` |
| 2026-09-17 | §7 DecisionPolicy thresholds; full `decisions/v1` catalog | Spec §7; permissions ≠ semantics |
| 2026-09-17 | §8 ContextAssembler bounds + LocalJobDispatcher; no Jev fallback | Spec §8 |
| 2026-09-17 | §9 ListeningController windows; StreamIntake capture-only | Spec §9 |
| 2026-09-17 | `ListeningScriptedMind` retained as harness bridge with keyword heuristics; coverage via explicit `segmentId` / controller | Spec: bridge for old harness; document in this file |
| 2026-09-17 | §10 process graphs under `Relay.Core.Processes` (avoid clash with existing WorkflowDefinition) | Spec §10 |
| 2026-09-17 | §11 RetentionService + CapabilityBundle activation | Spec §11 |
| 2026-09-17 | §12 RelayCompositionFactory; Desktop still uses SessionCoordinator until WinUI cutover | Spec §12; Linux cannot compile WinUI |
| 2026-09-17 | §13 verify-cloud/live/windows + 10 jev-* harness scenarios + 240 eval items | Spec §13 |

## §9 ListeningScriptedMind bridge

- Production coverage is owned by `ListeningController` + durable windows.
- `ListeningScriptedMind` remains for slice3 harness scenarios only: keyword → move mapping.
- Segment handling requires explicit `segmentId` (and optional `windowId`); the old first-pending fallback is removed.
- In-memory `_handledSegmentIds` was replaced by `PendingMindSegmentIds` filtering in `BuildSnapshot`.

## §12 Desktop remaining

- `RelayCompositionFactory` rejects scripted minds in `Production` mode; Desktop `ModelComposition.CaseRuntimeOptions` points at it.
- `MainWindow` / `App.xaml.cs` still construct `SessionCoordinator` for legacy paths — full removal deferred to Windows verification (`verify-windows.ps1`).
- Headless surface coverage is in `SurfaceCompositionTests`.
