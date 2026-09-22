# F2 evidence

Phase: real mobile composition  
Branch: `cursor/v1-testflight-finalization-45e9`

## Delivered

- `@relay/sqlite-core` shared engine store + migrations for Node and Expo
- `openExpoSqliteHandle()` via `expo-sqlite` `openDatabaseSync`
- `EncryptedArtifactStore` (AES-GCM key in the secret store; SQLite holds refs only)
- `createMobileClient.native.ts` production Expo composition (no testkit)
- Web stub `createMobileClient.ts` so desktop/web bundles do not load expo-sqlite
- Foreground speech fails closed (`speech_unavailable`) until a device recognizer reports ready
- Lifecycle suspend cancels capture on background

## Local gates

- `npm run test:unit` PASS (103)
- `adapters/node/src/mobile-rehydrate.integration.test.ts` PASS (reopen case + decrypt artifact; prose absent from sqlite bytes)
- store-contract integration PASS
- privacy suite PASS
- `tsc -b` PASS
- architecture PASS

## Not GREEN

Physical iPhone and Android create/close/relaunch/visible rehydrate is still required. Node sqlite proves the shared store and encryption contract; it is not a substitute for a custom native Expo client on a device.
