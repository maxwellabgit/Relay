# STATUS

What has been verified on the TypeScript/Tauri Windows path, and what has not.

## Current decision

Windows V1 completion is on branch `build/windows-v1-completion`. Architecture authority remains:

```text
TypeScript engine
    owns state transitions and product behavior

Jev
    answers bounded typed semantic questions

local model
    interprets / extracts / drafts / answers

deterministic code
    authorizes, schedules, persists, retries and executes

Tauri
    provides Windows storage, secrets, local system integration
```

Models never gain execution authority. The developer console projects runtime evidence; it does not invent a second decision system.

## Windows V1 — implemented

| Area | State |
| --- | --- |
| Protected artifacts | DPAPI object store under `%LOCALAPPDATA%\RELAY\objects\` via `TauriArtifactStore` |
| Secrets | DPAPI secret store (`secret_set` / `secret_status` / `secret_delete`); TypeSafe reads store, not env, in production |
| Feed / source prose | Content-addressed artifacts; SQLite holds refs only (`feed_items`, `source_events`) |
| Local model | `TauriLocalModelPort` → external loopback llama.cpp-compatible server (`external:ready` / `external:unavailable`); start via `./dev/start-model.ps1` |
| Hosted processing | Application grant `hosted_processing_enabled` (default OFF); key present ≠ disclosure; Settings gear toggles grant + TypeSafe key + health retry |
| Listen | Tauri `audio_*` commands + `relay_audio.live` NDJSON source; same `ingestFinalSegment` path as replay |
| Reflex | **One** production Reflex complete: `resolve-acronym@1` (bundled dictionary + memory + bounded Jev Choice) |
| Restart / timing | Durable SQLite + artifacts; canonical stages include `case.created`, `model.*`, `answer.committed`; run `manifest.json` + `events.jsonl` |
| Acceptance test | `windows-v1-production-path.acceptance.integration.test.ts` |

## Explicitly not claimed

- Google Calendar / Gmail / Sheets / GitHub / Plaid connectors wired end-to-end
- Working iPhone / TestFlight build
- Physical Halo hardware
- Four production Reflexes (only `resolve-acronym@1` is complete)
- Automatic Reflex code generation
- Hosted transcription or silent failover from local model to a cloud model

## Remaining limitations

- Live mic ASR depends on optional local packages (`sounddevice`, `faster-whisper`); without them Listen can start but may emit no finals until a fixture/`RELAY_AUDIO_FIXTURE` is used
- Local model answers only when an external loopback server is available on the configured port (`RELAY_LOCAL_MODEL_PORT`, default 8080). RELAY reports `external:unavailable` when absent and never claims Ask is ready without a capability check (`./dev/start-model.ps1 -StartHint`).
- Hosted Jev disclosure requires **Allow hosted processing** (default OFF) in addition to a TypeSafe key
- Manual Windows dogfood checklist in the build directive should still be exercised on a machine with model + optional ASR before calling the gate “shipped”

## Verification

```powershell
npm run verify:v1
```

See also `docs/WINDOWS_V1_ACCEPTANCE.md` for baseline/final SHAs and command results for this build.
