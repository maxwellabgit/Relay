# Windows V1 Acceptance Record

## Baseline

- Branch: `build/windows-v1-completion`
- Baseline `origin/main` SHA: `94ee7c042c0a31e824969b8f7dafe5ac6e8626c8`

## Commits created (focused stage sequence)

1. `06bb37a` — `security: protect relay artifacts and Windows secrets`
2. `7ef9107` — `model: wire local generation into Windows RELAY`
3. `216081e` — `listen: connect real local transcript intake`
4. `35bd649` — `reflex: complete resolve-acronym v1`
5. `e823538` — `runtime: harden Windows recovery and end-to-end timing`
6. (this commit) — `docs: record Windows V1 acceptance state`

## Final gate result

```text
npm run verify:v1
→ verify:v1 passed
```

All stage checks green: format, lint, typecheck, unit, architecture, integration, replay, privacy, web-export, ios-export, halo, cargo-fmt, cargo-clippy, cargo-test, smoke, desktop-build.

Final SHA is recorded in the docs commit that lands this file.

## What exists after this build

- Windows desktop composition uses `TauriArtifactStore` + SQLite + DPAPI secrets (not `MemoryArtifactStore`)
- Local model behind `TextModelPort` with truthful `model: ready|unavailable` status
- Listen starts/stops allowlisted `relay_audio.live` and drains finals into `RelayEngine.ingestFinalSegment`
- One complete Reflex: `resolve-acronym@1`
- Run layout: `%LOCALAPPDATA%\RELAY\runs\<run-id>\manifest.json` + `events.jsonl`
- Named acceptance test: `adapters/node/src/windows-v1-production-path.acceptance.integration.test.ts`

## Verification commands (Stage gates)

Recorded as green during implementation:

- `npm run format:check`
- `npm run lint`
- `npm run typecheck`
- `npm run test:unit`
- `npm run test:integration`
- `npm run test:privacy`
- `npm run test:replay`
- `npm run test:smoke`
- `cargo fmt --check` / `cargo clippy -- -D warnings` / `cargo test` (desktop)
- `npm run build:desktop`

Final gate:

```powershell
npm ci
npm run verify:v1
```

## Manual acceptance

The full 22-step desktop dogfood sequence from the build directive requires a local model server and (for spoken Listen) optional ASR packages. Automated coverage substitutes Windows-equivalent composition + fixture/mock ports where hardware is unavailable.

Manual checklist status for this handoff: **pending operator run** on a Windows machine with:

1. Local llama.cpp-compatible server on `127.0.0.1:8080` (or `RELAY_LOCAL_MODEL_PORT`)
2. Optional `sounddevice` + `faster-whisper` for live mic finals
3. TypeSafe key seeded via `secret_set` or one-time `RELAY_TYPESAFE_API_KEY` bootstrap into DPAPI

## Known remaining limitations

- Only one production Reflex is complete
- Connectors / Halo / iPhone remain out of scope for this gate
- Live ASR without installed Python deps will not invent transcript text (correct fail-closed behavior)

## Stop point

After `npm run verify:v1` passes on this branch, **stop**. Do not start Halo emulator or iPhone/TestFlight work until this branch is reviewed.
