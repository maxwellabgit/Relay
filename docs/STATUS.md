# STATUS

What has been verified on the TypeScript/Tauri Windows path, and what has not.

## Current decision

**V1 / TestFlight is NO-GO.** Reviewed baseline `1862daacc8d06c6bc367c85b4cd523779d99b8fa` is the historical matrix SHA. `main` is `8bc700b07c8a0e66c289d8bfb620204bdd0719b6`. verify:v1 passed on that tip, including a reported headed Windows restart of an accepted birthday. That run was not independently rechecked from its bundle here. It does not prove live Jev, live Calendar, mobile, Halo, or EAS release IDs.

Jev canary: do not mark G1 green. The capability matrix still says the live canary has not run. `docs/implementation/RELAY_V1_FINAL_RELEASE_WORKFLOW_31ffec5.md` disagrees with itself (finding 11 and gate G1 say the canary did not run; phase 10 records native-import HTTP 200). No committed canary file is proof.

Active workflow: `docs/implementation/RELAY_LIVE_JEV_TESTFLIGHT_FINAL_WORKFLOW_1862daa.md` (gates G0–G8).  
Historical finalization review: `docs/implementation/RELAY_V1_TestFlight_Finalization_Review_4b64928.md`.  
Live ledger: `docs/implementation/FINALIZATION_STATUS.md`.  
Product contract: `docs/V1_PRODUCT_CONTRACT.md`.  
Capability matrix: `packages/contracts/src/capabilities.ts`.

F0 and F1 remain historical GREEN. F2–F8 stay non-green. G0 is the active truth freeze. G1–G8 are open code and device gates, not human-only leftovers.

Architecture authority remains:

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
| Reflex | Four reviewed modules are registered. None is `shipped`. Acronym resolution still lacks authorized conversational context and a centralized `jev-latest` model |
| Restart / timing | Durable SQLite + artifacts; canonical stages include `case.created`, `model.*`, `answer.committed`; run `manifest.json` + `events.jsonl` |
| Acceptance test | `windows-v1-production-path.acceptance.integration.test.ts` |

## Explicitly not claimed

- Google Calendar / Gmail / Sheets / GitHub / Plaid connectors. A read port and safe draft actions exist in code. A live Google account is not connected. Send, delete, and edits of existing files are refused.
- Drafting mail or creating a new Google Sheet or Doc through that safe-draft gate. The provider call waits for a connected account and a user commit.
- Working iPhone / TestFlight build
- Physical Halo hardware
- Any capability marked `shipped` (none are, at `1862daa`)
- Live Jev receiving authorized transcript/context (ambient requests still send `{ origin: "observed" }`)
- Scoped disclosure grants, budgets, and Retry-After backoff
- On-device mobile model or mobile speech (`mobile_model_pending`, unavailable speech)
- Production bundle purity (`App.tsx` still imports `@relay/testkit/browser`)
- Twelve headed Windows journeys
- EAS project linkage (all-zero project id and `REPLACE_WITH_*` submission values)
- Automatic Reflex code generation
- Hosted transcription or silent failover from local model to a cloud model
- Manual Windows dogfood or installed-NSIS Listen smoke on this machine

## Remaining limitations

Code blockers at `1862daa`, in workflow order:

- G1: Jev protocol, disclosure grants, ambient/acronym context, retries, and diagnostics are incomplete.
- G2: `App.tsx` imports browser testkit data; note/fact/recommendation semantics need headed proof.
- G3: mobile model, speech, and native diagnostics are stubs or dev-only routes.
- G4: consumer boot/error states, fake waveform, and developer-console flavor enforcement.
- G5–G6: full CI lanes and twelve headed Windows journeys are not green at this SHA.
- G7–G8: Windows rehearsal, EAS/Apple setup, and TestFlight device acceptance are not started.

Operator limits that remain after those code gates:

- Live mic ASR depends on optional local packages (`sounddevice`, `faster-whisper`) and a locally present Whisper model. Listen fails closed (stays OFF) when the ASR source does not report `source.ready`.
- Replay remains available when audio is unavailable.
- Local model answers only when an external loopback server is available on the configured port (`RELAY_LOCAL_MODEL_PORT`, default 8080). RELAY reports `external:unavailable` when absent.
- A global hosted-processing boolean is not a scoped grant. Do not treat it as release authorization.

## Verification

```powershell
npm run verify:v1
npm run readiness:windows   # CODE READY (structural)
npm run readiness:dogfood   # READY FOR DOGFOOD (model + audio required)
```

See also `docs/WINDOWS_V1_ACCEPTANCE.md` for baseline/final SHAs and command results for this build.
