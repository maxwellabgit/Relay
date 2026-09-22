# V1 TestFlight Finalization Status

Release authority: `RELAY_V1_TestFlight_Finalization_Review_4b64928.md`.  
Update only this file for finalization phase state. Exact-SHA evidence is mandatory for GREEN.

## Identity

| Field | Value |
| --- | --- |
| Branch | `cursor/v1-testflight-finalization-45e9` |
| Reviewed tip | `4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6` |
| Implementation parent | `4a4529d1529adf6d6460e17e2797e02b16135182` |
| Exact-tip CI (reviewed foundation) | https://github.com/maxwellabgit/Relay/actions/runs/35727102376 PASS (`head_sha` = `4b64928…`) |
| Active phase | F2 (code landed; physical device rehydrate still required) |
| Release decision | **NO-GO / V1 BLOCKED** |
| Foundation | production-core Phases 0–8 are **FOUNDATION COMPLETE** only (not V1 complete) |

## Phase states

| Phase | State | Tip SHA (when GREEN) | Exact CI / evidence | Notes |
| --- | --- | --- | --- | --- |
| F0 Reopen + release truth | GREEN | `a80e8cc9f2b66e62e845e7efff1ac20d83a272fd` | https://github.com/maxwellabgit/Relay/actions/runs/35734873042 PASS | See `evidence/F0/` |
| F1 V1 contracts + matrix | GREEN | `3473b83195376ce5515efce4947f58355451ea2e` | https://github.com/maxwellabgit/Relay/actions/runs/35737611615 PASS | See `evidence/F1/` |
| F2 Mobile composition | DEVICE GATE | — | Node reopen + encrypted artifacts PASS locally | Physical iPhone/Android relaunch still required before GREEN |
| F3 Tiny model + Reflexes | BLOCKED | — | — | Awaits F2 |
| F4 UI lifecycle quality | BLOCKED | — | — | Awaits F3 |
| F5 Observability | BLOCKED | — | — | Awaits F4 |
| F6 Product proof tests | BLOCKED | — | — | Awaits F5 |
| F7 Privacy / store | BLOCKED | — | — | Awaits F6; human Apple metadata |
| F8 TestFlight RC | BLOCKED | — | — | Awaits F7; human credentials / devices |

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

## Next exact action

F2 code is in tree but not GREEN: run the physical iPhone and Android create/relaunch/rehydrate check on a custom native Expo client. Continue F3–F5 code that does not require devices; do not claim mobile production proof from Node alone.

## GREEN rule

A phase may be marked GREEN only when:

1. A successful GitHub Actions workflow exists with `head_sha` equal to the claimed tip SHA, and
2. Every gate listed for that phase in the review document has recorded evidence paths, and
3. No open blocker remains for that phase.
