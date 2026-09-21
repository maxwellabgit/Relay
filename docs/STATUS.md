# STATUS

What has been verified on the TypeScript/Tauri Windows path, and what has not.

## Current decision

Windows V1 release candidate is on branch `main`. Architecture authority remains:

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
| Protected artifacts | DPAPI object store under `%LOCALAPPDATA%\RELAY\objects\` via `TauriArtifactStore`; migration `008` scrubs legacy plaintext into artifacts |
| Secrets | DPAPI secret store (`secret_set` / `secret_status` / `secret_delete`); TypeSafe reads store, not env, in production |
| Feed / source prose | Content-addressed artifacts; SQLite holds refs only (`feed_items`, `source_events`, learning tables) |
| Local model | `TauriLocalModelPort` → external loopback llama.cpp-compatible server (`external:ready` / `external:unavailable`); start via `./dev/start-model.ps1` |
| Hosted processing | Application grant `hosted_processing_enabled` (default OFF); missing/throwing grant check fails closed; Settings gear toggles grant + TypeSafe key + health retry |
| Jev status chip | Evidence-based: `missing key` → `hosted off` → `configured` → `ready` / `degraded`. Only JudgmentPort success/failure updates provider evidence; audio/model refresh does not. |
| Listen | Tauri `audio_*` commands + `relay_audio.live` NDJSON source; mic finals require Listening ON. Listen fails closed and remains OFF when the local ASR source does not report `source.ready`. |
| Replay | Developer fixture replay uses `ingestReplayFinalSegment` — independent of Listen and audio health; does not set Listening or start audio |
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
- Manual Windows dogfood or installed-NSIS Listen smoke on this machine

## Remaining limitations

- Live mic ASR depends on optional local packages (`sounddevice`, `faster-whisper`) and a locally present Whisper model. Listen fails closed (stays OFF) when the ASR source does not report `source.ready`.
- Replay remains available when audio is unavailable.
- Local model answers only when an external loopback server is available on the configured port (`RELAY_LOCAL_MODEL_PORT`, default 8080). RELAY reports `external:unavailable` when absent and never claims Ask is ready without a capability check (`./dev/start-model.ps1 -StartHint`).
- Hosted Jev disclosure requires **Allow hosted processing** (default OFF) in addition to a TypeSafe key
- Manual Windows dogfood checklist should still be exercised on a machine with model + ASR before calling the gate “shipped” (`npm run readiness:dogfood`)

## Verification

```powershell
npm run verify:v1
npm run readiness:windows   # CODE READY (structural)
npm run readiness:dogfood   # READY FOR DOGFOOD (model + audio required)
```

See also `docs/WINDOWS_V1_ACCEPTANCE.md` for baseline/final SHAs and command results for this build.
