# V1 TestFlight Finalization Status

Release authority: `RELAY_LIVE_JEV_TESTFLIGHT_FINAL_WORKFLOW_1862daa.md`.  
Historical review: `RELAY_V1_TestFlight_Finalization_Review_4b64928.md`.  
Update this file for gate state. Exact-SHA evidence is mandatory for GREEN.  
Capability labels are only `shipped`, `degraded`, `not-shipped`, or `unverified-on-device`.

## Identity

| Field | Value |
| --- | --- |
| Branch | `cursor/live-jev-g0-truth-45e9` |
| Reviewed baseline | `1862daacc8d06c6bc367c85b4cd523779d99b8fa` (`main`) |
| Historical foundation tip | `4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6` |
| Exact-tip CI (reviewed baseline) | https://github.com/maxwellabgit/Relay/actions/runs/35742750378 PASS (`head_sha` = `1862daa…`, verify:v1 only) |
| Toolchain at G0 | Node `v22.14.0`, npm `10.9.7`, rustc `1.83.0`, cargo `1.83.0` |
| Active gate | G1 Live Jev (canary blocked); later code slices continue where a Linux VM can prove them |
| Release decision | **NO-GO / V1 BLOCKED** |
| Foundation | production-core Phases 0–8 are foundation only. F0 and F1 are historical GREEN. They are not V1. |

## Phase states

| Phase | State | Tip SHA (when GREEN) | Exact CI / evidence | Notes |
| --- | --- | --- | --- | --- |
| F0 Reopen + release truth | GREEN | `a80e8cc9f2b66e62e845e7efff1ac20d83a272fd` | https://github.com/maxwellabgit/Relay/actions/runs/35734873042 PASS | See `evidence/F0/` |
| F1 V1 contracts + matrix | GREEN | `3473b83195376ce5515efce4947f58355451ea2e` | https://github.com/maxwellabgit/Relay/actions/runs/35737611615 PASS | See `evidence/F1/` |
| F2 Mobile composition | DEVICE GATE | — | Node reopen + encrypted artifacts PASS locally | Not GREEN: physical relaunch plus G3 stubs |
| F3 Tiny model + Reflexes | IN PROGRESS | — | One local typed repair at `f94a4bb`; model tournament not run | Not GREEN: G1 canary and G3 device tournament |
| F4 UI lifecycle quality | CODE LANDED | — | Boot surfaces and internal-channel console gate at `5f83b2f` | Not GREEN: visual, keyboard, screen-reader, device a11y |
| F5 Observability | CODE LANDED | — | Judgment evidence fields at `d634121`; typed corpus at `f94a4bb` | Not GREEN: CI lanes are not all green at one SHA |
| F6 Product proof tests | BLOCKED | — | — | Not GREEN: twelve headed journeys are not implemented |
| F7 Privacy / store | BLOCKED | — | — | Awaits F6; human Apple metadata |
| F8 TestFlight RC | BLOCKED | — | — | Awaits the G8 exit, not a human-only leftover |

F2–F8 stay non-green. “CODE LANDED” is not GREEN. Exact-tip verify:v1 on `1862daa` does not close F5.

## Active gates (G0–G8)

| Gate | State | What blocks GREEN |
| --- | --- | --- |
| G0 Freeze the truth | GREEN | `e281f2ce4bbbad17c4be0720dc6bd92839f12107` — see `evidence/G0/` |
| G1 Live Jev | OPEN | Protocol, grant, rounds, and judgment evidence landed. Budget before and after landed at `5fc29ec`. Still open: live canary |
| G2 Core semantics | OPEN | Semantics and export purity landed at `589598a`. Still open: Windows installer and headed product proof |
| G3 Mobile model, speech, diagnostics | OPEN | Trace and fail-closed listening at `80f741b`. Speech suspend and relaunch at `0be8c6f`. One local typed repair at `f94a4bb`. Prepared audio replay at `ea14915`. Pending mobile transport removed at `c0850bb`. Model delivery with no pin at `844c544`. Still open: model tournament, real speech, both phones |
| G4 UI and developer console | OPEN | Boot surfaces at `5f83b2f`. Console filters, copy, and export at `d0cca3b`. Composer draft retained on a refused send at `27aeabf`. Still open: visual matrix, keyboard-only Windows, screen reader, screenshot diffs |
| G5 Production CI lanes | OPEN | Deterministic typed corpus at `f94a4bb`. Prepared audio replay at `ea14915`. Still open: Rust, headed, pinned speech and model, and every lane at one SHA |
| G6 Headed Windows journeys | OPEN | One deterministic journey only; eleven product journeys missing |
| G7 Windows release rehearsal | OPEN | Human script after G6; not started |
| G8 TestFlight | OPEN | Zero EAS project id, `REPLACE_WITH_*`, Apple/EAS login, device acceptance |

## Honest proof inventory (at F0 start)

| Journey ID | Title | Current proofKind | Harness |
| --- | --- | --- | --- |
| 01 | Deterministic glossary | headed-product | `tools/e2e/msrp-desktop-headed.ts` |
| 02 | Helpful chat | integration-lower-layer | `adapters/node/src/tool-kernel.integration.test.ts` |
| 03 | Tool-assisted Ask | integration-lower-layer | same |
| 04 | Ambient ignore | integration-lower-layer | `adapters/node/src/ambient-triage.integration.test.ts` |
| 05 | Ambient note recommendation | integration-lower-layer | same |
| 06 | Foreground listening | missing-harness | — (must be product-proven in F6) |
| 07 | Ambiguous acronym | integration-lower-layer | `adapters/node/src/bounded-expansion.integration.test.ts` |
| 08 | Claim verification | integration-lower-layer | `adapters/node/src/claim-verify.integration.test.ts` |
| 09 | Crash / restart | integration-lower-layer | `adapters/node/src/windows-v1-crash-recovery.integration.test.ts` |
| 10 | Write authority | integration-lower-layer | `adapters/node/src/tool-kernel.integration.test.ts` |
| 11 | Pattern proposal | integration-lower-layer | `adapters/node/src/bounded-expansion.integration.test.ts` |
| 12 | Shadow activation | integration-lower-layer | same |

Integration-lower-layer proofs are retained as the lower test layer. They are **not** V1 product proof. Headed Windows + physical mobile gates belong to F6.

## Evidence log

| When (UTC) | Command / event | Result |
| --- | --- | --- |
| 2026-09-22 | Reviewed exact-tip Actions `35727102376` on `4b64928` | PASS (verify:v1 only; not V1 product complete) |
| 2026-09-22 | Prior parent-SHA Actions `35684028985` on `4a4529d` | Historical PASS — must not be cited as tip proof for `4b64928` docs |
| 2026-09-22 | F0 commit `0e3fe8d` / lint fix `a80e8cc` | Local gates PASS |
| 2026-09-22 | Exact-tip Actions `35734873042` on `a80e8cc` | PASS — F0 GREEN |
| 2026-09-22 | Exact-tip Actions `35737611615` on `3473b83` | PASS — F1 GREEN |
| 2026-09-22 | F2 mobile composition | Shared sqlite-core + encrypted artifacts + createMobileClient.native; device proof outstanding |
| 2026-09-22 | Exact-tip Actions `35742750378` on `1862daa` | PASS — verify:v1 only. Does not green G1–G8 or F2–F8 |
| 2026-09-22 | Review verdict against `1862daa` | NO-GO. Remaining work is code plus device, not human-only |
| 2026-09-22 | G0 commit `e281f2c` | Local capability unit test PASS (12). Status docs agree. G1–G8 remain open |
| 2026-09-22 | G1 protocol slice `8a5746b` | Unit 119 and architecture 18 PASS. `tsc -b` exit 0. Gate remains OPEN |
| 2026-09-22 | G1 grant slice `b9e3247` | Unit 123, architecture 18, integration 69 PASS. `tsc -b` exit 0. Gate remains OPEN |
| 2026-09-22 | G1 grant command `33251e5` | Unit 124, architecture 18, integration 70 PASS. `tsc -b` exit 0. Gate remains OPEN |
| 2026-09-22 | G1 rounds and resume `ba54708` | Unit 126, architecture 18, integration 70 PASS. `tsc -b` exit 0. Gate remains OPEN |
| 2026-09-22 | G2 semantics and bundle `589598a` | `tsc -b` exit 0. Unit 128, architecture 21, integration 74, replay 2, privacy 5 PASS. Web/iOS/Android exports omit the fixture. Gate remains OPEN |
| 2026-09-22 | G3 mobile diagnostics `80f741b` | `tsc -b` exit 0. Unit 130, architecture 21, integration 75, replay 2, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G4 boot and console `5f83b2f` | `tsc -b` exit 0. Unit 136, architecture 21, integration 75, replay 2, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G4 console filters `d0cca3b` | `tsc -b` exit 0. Unit 139, architecture 21, integration 75, replay 2, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G3 speech session `0be8c6f` | `tsc -b` exit 0. Unit 142, architecture 21, integration 76, replay 2, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G1 judgment evidence `d634121` | `tsc -b` exit 0. Unit 143, architecture 21, integration 76, replay 2, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G5 typed corpus `f94a4bb` | `tsc -b` exit 0. Unit 147, architecture 21, integration 76, replay 2, privacy 5 PASS. G3 and G5 remain OPEN |
| 2026-09-22 | G5 recorded audio `ea14915` | `tsc -b` exit 0. Unit 147, architecture 21, integration 76, replay 3, privacy 5 PASS. G3 and G5 remain OPEN |
| 2026-09-22 | G3 pending transport `c0850bb` | `tsc -b` exit 0. Unit 148, architecture 21, integration 76, replay 3, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G3 model delivery `844c544` | `tsc -b` exit 0. Unit 153, architecture 21, integration 76, replay 3, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G4 composer draft `27aeabf` | `tsc -b` exit 0. Unit 155, architecture 21, integration 76, replay 3, privacy 5 PASS. Gate remains OPEN |
| 2026-09-22 | G1 judgment budget `5fc29ec` | `tsc -b` exit 0. Unit 155, architecture 21, integration 76, replay 3, privacy 5 PASS. Gate remains OPEN |

## Next exact action

Do not mark G1 GREEN. Judgment evidence landed at `d634121`. Budget before and after landed at `5fc29ec`. The live canary stays blocked until a human imports the key outside this agent. Do not mark G2 GREEN. The Windows installer and headed journeys are still open. Do not mark G3 GREEN. Trace and fail-closed listening landed at `80f741b`. Speech suspend and relaunch landed at `0be8c6f`. One local typed repair landed at `f94a4bb`. A prepared audio-file replay landed at `ea14915`. The unused pending mobile transport was removed at `c0850bb`. Model delivery with no pin landed at `844c544`. The on-device model tournament and real speech still need an iPhone 15 Pro Max and a Galaxy S23 Ultra. Do not mark G4 GREEN. Boot surfaces landed at `5f83b2f`. Console filters, copy, and export landed at `d0cca3b`. A refused send keeps the composer draft at `27aeabf`. Visual, keyboard-only, screen-reader, and device proof are still open. Do not mark G5 GREEN. The typed corpus and the prepared audio replay are deterministic lower layers only. No speech package is pinned.

## GREEN rule

A phase may be marked GREEN only when:

1. A successful GitHub Actions workflow exists with `head_sha` equal to the claimed tip SHA, and
2. Every gate listed for that phase in the review document has recorded evidence paths, and
3. No open blocker remains for that phase.
