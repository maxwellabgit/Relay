# PRODUCTION CORE RESULTS

Final release proof for `cursor/relay-production-core`.

## Status

COMPLETE locally through Phase 8 Bugbot PASS. Windows V1 gates exercised on this machine; GitHub Actions will re-confirm on the Phase 8 push.

## Outcome summary

RELAY production-core delivers helpful Ask (tools + claim verify), ambient triage with wired recommendation cards, bounded Reflex proposal/activation with Library controls, live diagnostics, and a responsive product surface usable without the developer console. Windows headed MSRP E2E, NSIS silent-install smoke, soak, crash-recovery, and golden journey integration proofs passed locally.

## Branch and final SHA

- Branch: `cursor/relay-production-core`
- Final SHA: filled after Phase 8 commit

## Starting SHA and named commits

- Start: `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7`
- Named: `9fce112`, `05997c1`
- Preservation tag: `relay-dotnet-a6bf987`

## Phase table

| Phase | State |
| --- | --- |
| 0–7 | GREEN (see PRODUCTION_CORE_STATUS.md) |
| 8 Professional UI + Windows hardening | GREEN (Bugbot PASS; local release gates PASS) |

## Verification table

| Gate | Result |
| --- | --- |
| `npm run verify:v1` | PASS |
| `npm run test:e2e:msrp` | PASS |
| `npm run test:e2e:golden` | PASS (28 tests) |
| `npm run test:soak` | PASS |
| `npm run test:nsis:smoke` | PASS |
| Phase 8 Bugbot | PASS |

## Golden E2E table

| Journey | Proof |
| --- | --- |
| 01 Deterministic glossary | Headed Tauri `test:e2e:msrp` |
| 02–03 Helpful / tool Ask | tool-kernel integration |
| 04–05 Ambient ignore / note | ambient-triage integration |
| 07 Ambiguous acronym | bounded-expansion integration |
| 08 Claim verification | claim-verify integration |
| 09 Crash / restart | windows-v1-crash-recovery |
| 10 Write authority | tool-kernel / operations |
| 11–12 Pattern + activation | bounded-expansion lifecycle |

## Installed NSIS result

PASS — silent install + launch recorded in `.dev-data/nsis-smoke-latest/result.json`.

## Remaining blockers

- Full headed automation for journeys 2–12 remains Node-integration backed; journey 1 is headed desktop.
- Live model/audio/Jev dogfood remains operator-driven via `readiness:dogfood` (not claimed automated).

## Windows V1 stop before iPhone

Windows V1 gates are the release bar. iPhone/Expo productization starts only after CI is green on the Phase 8 tip.
