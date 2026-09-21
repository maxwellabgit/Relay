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

Status: **pending** — not claimed by the production-core automated gate.

## Installed NSIS smoke

Status: **NOT_RUN** on the production-core branch until Phase 8 release proof.

## Known remaining limitations

- Only one production Reflex (`resolve-acronym@1`) is complete
- Most connector/operation/approval commands still return `unsupported_command`
- Live diagnostics (`latest.json`) land in Phase 2
- Headed E2E covers deterministic MSRP only in Phase 0; full golden journeys are Phase 8

## Stop point

Production-core work proceeds phase-by-phase on `cursor/relay-production-core`. Windows V1 remains the release target; iPhone productization does not start until Windows V1 gates are green.
