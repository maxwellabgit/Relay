# PRODUCTION CORE RESULTS

Foundation release proof for `cursor/relay-production-core` (merged).  
**Not a V1 or TestFlight completion record.** See `FINALIZATION_STATUS.md`.

## Status

**FOUNDATION COMPLETE / V1 BLOCKED.**

Production-core Phases 0–8 delivered a coherent TypeScript engine foundation. Tip `4b64928` has exact-tip verify:v1 PASS (Actions `35727102376`). That is **not** V1 product completion.

## Outcome summary

RELAY production-core delivers a durable desktop engine path (Ask tools, ambient triage contracts, bounded Reflex proposal/activation, live diagnostics, shared product surface). Windows headed MSRP (journey 01) and Node integration lower-layer suites exist. Mobile remains an in-memory demo. Journeys 02–12 are not headed product proofs. Journey 06 has no harness. Soak and NSIS smoke do not prove full product workflows.

## Branch and SHAs

- Foundation tip: `4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6`
- Implementation parent: `4a4529d1529adf6d6460e17e2797e02b16135182`
- Exact-tip CI: https://github.com/maxwellabgit/Relay/actions/runs/35727102376
- Historical parent CI (do not cite as tip proof): https://github.com/maxwellabgit/Relay/actions/runs/35684028985

## Verification table (foundation only)

| Gate | Result | Meaning |
| --- | --- | --- |
| Exact-tip `npm run verify:v1` | PASS on `4b64928` | Source/CI gate only |
| `npm run test:e2e:msrp` | Historical PASS | Journey 01 headed |
| `npm run test:e2e:golden` | Lower-layer integration only | Not headed 02–12 |
| `npm run test:soak` | ~45s Node Ask loop | Not 30-minute product soak |
| `npm run test:nsis:smoke` | Install + short process survival | Not installed functional journey |

## Remaining V1 blockers (non-exhaustive)

See finalization review findings. Highlights:

- No `createMobileClient()`; Expo adapter placeholder
- No on-device mobile model; no mobile SQLite/artifact/Jev/audio production path
- Connectors/tools often fixtures; hide or wire with receipts
- Only one production Reflex (`resolve-acronym@1`)
- Golden journeys incomplete / mostly integration
- Diagnostics live-summary hard-coded unknowns
- Store/EAS/privacy not ready; background audio claimed early

## Windows V1 stop before iPhone

Windows product-proof gates (F6) must pass before claiming TestFlight readiness. Foundation CI green alone does not authorize iPhone/Expo productization claims.
