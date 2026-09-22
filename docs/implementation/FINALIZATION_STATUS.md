# V1 TestFlight Finalization Status

Release authority: `RELAY_V1_TestFlight_Finalization_Review_4b64928.md`.  
Update only this file for finalization phase state. Exact-SHA evidence is mandatory for GREEN.

## Identity

| Field | Value |
| --- | --- |
| Branch | `cursor/v1-testflight-finalization-45e9` |
| Reviewed tip | `4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6` |
| Implementation parent | `4a4529d1529adf6d6460e17e2797e02b16135182` |
| Exact-tip CI (reviewed) | https://github.com/maxwellabgit/Relay/actions/runs/35727102376 PASS (`head_sha` = `4b64928…`) |
| Active phase | F0 |
| Release decision | **NO-GO / V1 BLOCKED** |
| Foundation | production-core Phases 0–8 are **FOUNDATION COMPLETE** only (not V1 complete) |

## Phase states

| Phase | State | Tip SHA (when GREEN) | Exact CI / evidence | Notes |
| --- | --- | --- | --- | --- |
| F0 Reopen + release truth | GATES LOCAL / AWAITING EXACT-TIP CI | (pending push tip) | local format/arch/unit/golden PASS; CI pending | See `evidence/F0/` |
| F1 V1 contracts + matrix | BLOCKED | — | — | Awaits F0 |
| F2 Mobile composition | BLOCKED | — | — | Awaits F1 |
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
| 2026-09-22 | F0 reopen started on `cursor/v1-testflight-finalization-45e9` | IN PROGRESS |

## Next exact action

Complete F0: commit reopen + manifests + CI/verify hardening; run local architecture/golden gates; push for exact-tip CI ≤45 minutes.

## GREEN rule

A phase may be marked GREEN only when:

1. A successful GitHub Actions workflow exists with `head_sha` equal to the claimed tip SHA, and
2. Every gate listed for that phase in the review document has recorded evidence paths, and
3. No open blocker remains for that phase.
