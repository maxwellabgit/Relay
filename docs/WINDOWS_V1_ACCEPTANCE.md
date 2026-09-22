# Windows V1 Acceptance Record

## Current decision

**V1 blocked.** Reviewed tip `1862daacc8d06c6bc367c85b4cd523779d99b8fa` has exact-tip verify:v1 PASS on `main` (Actions `35742750378`). That is a source gate only. Headed journeys 02–12, live Jev, the Windows installer, and physical devices are unverified. Active authority is `docs/implementation/RELAY_LIVE_JEV_TESTFLIGHT_FINAL_WORKFLOW_1862daa.md`. Historical foundation tip `4b64928` (Actions `35727102376`) stays in the record below and is not the release SHA.

## Current production-core baseline

- Branch (implementation): `cursor/relay-production-core` (merged to `main`)
- Reviewed tip: `4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6`
- Implementation parent: `4a4529d1529adf6d6460e17e2797e02b16135182`
- Historical anchors: `05997c1`, `9fce112`
- Architecture authority: TypeScript engine + Tauri Windows adapters
- Preservation tag for retired .NET stack: `relay-dotnet-a6bf987`

## Honest CI record for `05997c1`

GitHub Actions `check` for `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7` **failed**:

- Run: `35629950586` (2026-09-21)
- Failure step: `npm run test:smoke`
- Root cause: commit `9fce112` replaced the `test:smoke` package script with `test:manual:msrp` while `.github/workflows/check.yml` and `tools/verify-v1.mjs` still invoked `test:smoke`. The desktop build step was skipped because the job stopped at smoke.

Production-core Phase 0 restores `test:smoke`, keeps `test:manual:msrp` separate, and routes both local `verify:v1` and GitHub Actions through one shared verification manifest.

## Exact-tip CI for `4b64928`

| Check | Result |
| --- | --- |
| GitHub Actions `35727102376` | PASS (`head_sha` = `4b64928…`) |
| Meaning | verify:v1 source gate only — not headed 02–12, mobile, or store proof |

## Prior freeze tip (last known green before MSRP commits)

**Final main SHA (CI-green tip before `9fce112`/`05997c1`):** `9237836042793dc28ced794b1066fe7a0a4b6438`

| Check | Result |
| --- | --- |
| `npm run verify:v1` | PASS (historical) |
| GitHub Actions for `9237836` | PASS (run `35552370959`) |

## Verification commands

```powershell
npm ci
npm run verify:v1              # shared manifest (includes smoke + desktop build)
npm run test:manual:msrp       # Node-harness preflight — not desktop E2E
npm run test:e2e:msrp          # Headed Tauri MSRP (journey 01 only)
npm run test:e2e:golden        # Integration lower layer only (not V1 product proof)
```

Shared manifest: `tools/verification/manifest.mjs`.  
Golden IDs: `tools/e2e/golden-journeys.manifest.mjs`.

## Manual Windows dogfood

Status: **operator-driven** via `npm run readiness:dogfood` — not claimed by the automated gate.

## Installed NSIS smoke

Status: historical foundation PASS (process survival) — not an installed functional product journey (F6).

## Headed Windows E2E

Status: journey **01** headed PASS historically. Journeys **02–12** are not headed product proofs; **06** has no harness.

## Reviewed SHA `1862daa`

| Check | Result |
| --- | --- |
| GitHub Actions `35742750378` on `main` | PASS (`head_sha` = `1862daacc8d06c6bc367c85b4cd523779d99b8fa`) |
| GitHub Actions `35742747165` on the finalization branch | PASS (same SHA) |
| Meaning | verify:v1 source gate only. Not G1 live Jev, not twelve headed journeys, not TestFlight |

Toolchain recorded with G0 evidence: Node `v22.14.0`, npm `10.9.7`, rustc `1.83.0`, cargo `1.83.0`.

## Known remaining limitations

- Jev ambient disclosure, model id, scoped grants, and retries are code blockers (G1).
- Production still imports browser testkit data (G2).
- Mobile model, speech, diagnostics, and EAS placeholders are code blockers (G3, G8).
- Headed product proof is journey 01 only. Golden lower-layer tests are not headed journeys.
- Live model/audio/Jev dogfood remains unverified on Windows.

## Stop point

Execute G0–G8 in `docs/implementation/RELAY_LIVE_JEV_TESTFLIGHT_FINAL_WORKFLOW_1862daa.md`. Do not claim V1 or TestFlight until those gates are green at one exact SHA with recorded artifacts. F2–F8 stay non-green until their exits in that workflow are met.
