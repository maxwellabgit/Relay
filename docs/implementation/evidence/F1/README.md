# F1 evidence

Phase: Freeze V1 contracts and platform matrix  
Branch: `cursor/v1-testflight-finalization-45e9`

## Delivered

- `docs/V1_PRODUCT_CONTRACT.md` — included/excluded boundary + composition rules
- `packages/contracts/src/capabilities.ts` — `V1_CAPABILITY_MATRIX` (`real` / `degraded` / `test-only` / `not-shipped`)
- Permanent IDs: `app.relay.assistant` (mobile), `app.relay.desktop` (desktop)
- Marketing version **1.0.0** on Expo app config + Tauri conf + `@relay/app`
- `createAppClient` fail-closed on native/mobile without demo flag; demo only with `EXPO_PUBLIC_RELAY_ALLOW_DEMO=1`
- Composition purity unit tests (no testkit in desktop / app client selector)
- Removed premature iOS `UIBackgroundModes: audio` to match foreground-only V1 listening contract

## Local gates

Recorded at commit time.

## Exact-tip CI

Record after push — GREEN requires Actions `head_sha` equal to F1 tip.
