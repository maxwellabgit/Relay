# Windows V1 Acceptance Record

## Current production-core baseline

- Branch (implementation): `cursor/relay-production-core`
- Historical main tip reviewed: `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7`
- Included history: `9fce11241019321d3d326c5b37f7578efc8d5630` (deterministic MSRP path) + `05997c1` (Ask text in developer console)
- Architecture authority: TypeScript engine + Tauri Windows adapters
- Preservation tag for retired .NET stack: `relay-dotnet-a6bf987`

## Honest CI record for `05997c1`

GitHub Actions `check` for `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7` **failed**:

- Run: `35629950586` (2026-09-21)
- Failure step: `npm run test:smoke`
- Root cause: commit `9fce112` replaced the `test:smoke` package script with `test:manual:msrp` while `.github/workflows/check.yml` and `tools/verify-v1.mjs` still invoked `test:smoke`. The desktop build step was skipped because the job stopped at smoke.

Production-core Phase 0 restores `test:smoke`, keeps `test:manual:msrp` separate, and routes both local `verify:v1` and GitHub Actions through one shared verification manifest.

## Prior freeze tip (last known green before MSRP commits)

**Final main SHA (CI-green tip before `9fce112`/`05997c1`):** `9237836042793dc28ced794b1066fe7a0a4b6438`

| Check | Result |
| --- | --- |
| `npm run verify:v1` | PASS (historical) |
| GitHub Actions for `9237836` | PASS (run `35552370959`) |

## Verification commands (Phase 0+)

```powershell
npm ci
npm run verify:v1              # shared manifest (includes smoke + desktop build)
npm run test:manual:msrp       # Node-harness preflight — not desktop E2E
npm run test:e2e:msrp          # Headed Tauri MSRP with isolated profile
```

Shared manifest: `tools/verification/manifest.mjs`.

## Manual Windows dogfood

Status: **operator-driven** via `npm run readiness:dogfood` — not claimed by the automated gate.

## Installed NSIS smoke

Status: **PASS** on production-core Phase 8 — `.dev-data/nsis-smoke-latest/result.json` (`silent_install_and_launch_ok`).

## Headed Windows E2E

Status: **PASS** — `npm run test:e2e:msrp` evidence under `.dev-data/e2e/msrp-headed-latest/`.

## Known remaining limitations

- Golden journeys 2–12 are proven via integration harnesses (`npm run test:e2e:golden`); only journey 1 is headed desktop UI.
- Live model/audio/Jev dogfood remains a manual operator gate.
## Stop point

Production-core work proceeds phase-by-phase on `cursor/relay-production-core`. Windows V1 remains the release target; iPhone productization does not start until Windows V1 gates are green.
