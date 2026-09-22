# RELAY V1 TestFlight Finalization Review

Reviewed target: `4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6`  
Implementation parent: `4a4529d1529adf6d6460e17e2797e02b16135182`  
Historical anchors: `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7`, `9fce11241019321d3d326c5b37f7578efc8d5630`  
Review date: 2026-09-22

**This document is the release authority for V1 / TestFlight finalization.**  
Do not mark V1 complete from production-core ledgers alone. Track live phase state in `FINALIZATION_STATUS.md`.

## Executive decision

`4b64928` is not a production V1 or a TestFlight candidate. It is a documentation-only commit that prematurely marks the production-core project complete. The work since `05997c1` is a strong architectural recovery and must be preserved. The release decision is **NO-GO** until every phase gate F0–F8 passes with exact-SHA evidence (and physical-device evidence where required).

## What remains good (do not replace)

- Deterministic code owns state, policy, scheduling, permissions, idempotency, retries, execution, and recovery.
- Jev receives bounded typed Choice / Noul / Score questions only; no execution authority.
- Local text model sits behind `TextModelPort` and fails closed when absent.
- Hosted processing is separate from Listening and defaults off.
- Desktop uses SQLite, protected artifacts, DPAPI secrets, real Tauri commands, structural logs.
- Runtime events use correlation IDs and reject free-form transcript text.
- Ambient recommendation and Reflex lifecycle contracts exist and are testable.
- Consumer surface and developer console share the same engine snapshot.

## Findings (release impact)

| Severity | Finding | Required correction |
| --- | --- | --- |
| Blocker | Mobile is an in-memory demo (`createBrowserDemoClient` when Tauri absent) | Real `createMobileClient()` as Expo default; demo only behind explicit flag |
| Blocker | Expo adapter is a placeholder | Implement mobile ports (SQLite, secrets, artifacts, audio, diagnostics, model) |
| High | Completion record overstated proof (`4b64928` vs parent SHA/run) | Reopen status; exact-tip SHA/run mandatory; reserve “V1 complete” for product + device gates |
| Blocker | Tools/connectors mostly contracts/fixtures | Wire real adapters or hide capabilities; receipt before success |
| Blocker | No on-device mobile model | Native model port; benchmark 0.5B–1.7B 4-bit; select by gates |
| Blocker | iOS config not submittable | Real EAS/ASC IDs, version/build policy, privacy metadata |
| High | Golden E2E mostly Node integration; journey 06 missing | Twelve headed Windows journeys + physical mobile suite |
| High | Headed Ask bypasses real text entry via React fiber | Supported user events only |
| High | Restart proof shallow | Relaunch + visible rehydration |
| High | Diagnostics hard-coded unknowns | Derive live-summary from events/store |
| High | Mobile exposes Dev UI; settings call Tauri-only helpers | Dev only in internal builds; mobile settings on mobile adapters |
| High | Phone UI lacks lifecycle fundamentals | Safe areas, keyboard, AppState, error boundary, virtualized list |
| High | Soak / NSIS do not test product workflow | 30-minute headed soak + installed functional journey |
| High | Only one production Reflex | Four reviewed modules: acronym, note, fact, next-action |
| Medium | iOS gate is only JS export | Native prebuild, EAS, IPA, physical TestFlight evidence |
| Medium | UI state feedback incomplete | Busy/cancel/retry/errors; real waveform or none |
| Medium | iOS background audio claimed early | Foreground listening only for V1; remove background mode |

## Honest V1 product boundary

See `docs/V1_PRODUCT_CONTRACT.md` (F1). Summary: helpful local-first assistant; typed chat; explicit foreground listening; on-device tiny model; Jev behind consent; deterministic local memory/note/task/claim/Reflex actions; four reviewed Reflexes; no always-on mic; no silent cloud model fallback; no arbitrary Jev tools; no auto Reflex codegen; no external writes without receipt.

## Mobile model

Benchmark 0.5B / 1B–1.1B / 1.5B–1.7B instruct at 4-bit. Do not preselect. Native inference behind one `TextModelPort`; evaluate llama.cpp and ExecuTorch as replaceable implementations. A custom native Expo client build is required (Expo Go cannot validate the production runtime).

Acceptance budget (floors: iPhone 15 Pro Max, 8 GB S23 Ultra): ≤1.2 GB download; ≤2.8 GB peak / ≤2.2 GB steady; 2048 context / 256 out; cold p95 ≤4s; warm TTFT p95 ≤2s; ≥12 tok/s median; ≥98% valid typed output after one repair; cancel ≤250 ms; no crash/jetsam in 15-minute mixed run.

## Phase gates (ordered)

| Phase | Name | Exit |
| --- | --- | --- |
| F0 | Reopen + enforce release truth | Exact-tip CI ≤45m; docs describe what is proven |
| F1 | Freeze V1 contracts + matrix | One capability manifest drives claims/UI/tests/adapters |
| F2 | Real mobile composition | Physical relaunch rehydrates; no demo/testkit in bundle |
| F3 | Tiny model + four Reflexes + honest connectors | Offline core works; every side effect receipted |
| F4 | NepTranslate-quality UI patterns | Safe-area/keyboard/a11y matrix; no production Dev drawer |
| F5 | Observability | Cursor can explain current case/blocker without private prose |
| F6 | Product proof tests | Twelve headed Windows + soak + physical mobile journeys |
| F7 | Privacy / store readiness | Metadata matches runtime; no placeholders/secrets |
| F8 | TestFlight RC | `relay-v1.0.0-rc1` + seven clean days → V1 TESTFLIGHT VERIFIED |

Human-only actions: Apple/EAS credentials, physical-device runs, legal/privacy answers, TestFlight/App Review decisions.

## Exact-SHA evidence rule

A status row may say GREEN only when a successful workflow exists whose `head_sha` equals the candidate SHA being claimed. Parent-SHA greens are historical, not tip proof.
