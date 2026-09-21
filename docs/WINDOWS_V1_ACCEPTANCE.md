# Windows V1 Acceptance Record

## Baseline

- Branch: `wrap/windows-v1-final` (merging to `main`)
- Wrap baseline SHA: `88d5437cb40de2941eb021d7d38b392664707579`
- Architecture authority: TypeScript engine + Tauri Windows adapters

## Wrap-up commits

1. `2cd857d` — `privacy: migrate legacy prose into protected artifacts`
2. `3899bf5` — `replay: decouple developer fixtures from live listening`
3. `b3a44a4` — `authority: fail closed on hosted judgment dispatch`
4. `6170013` — `authority: make Jev status evidence based`
5. `d55208e` — `docs: reconcile Windows V1 release-candidate state`
6. `6250c79` — `dev: add Windows V1 readiness gate`
7. `e7a0b5b` (+ tip amendments) — crash-recovery wait / readiness PS 5.1 / clippy

Final tip on wrap branch before merge: see `git rev-parse HEAD` after landing on `main`.

## Automated gates (this session)

```text
npm run verify:v1
→ verify:v1 passed

npm run readiness:windows
→ readiness:windows passed (diagnostic)
  optional: local model not reachable on :8080
  secrets not inspected
```

GitHub Actions `check` for the final main SHA: record after push.

## Manual Windows dogfood

Status: **pending** — not run on this machine.

| Prerequisite | Observed this session |
| --- | --- |
| Local llama.cpp (`127.0.0.1:8080`) | Unavailable |
| `python -m relay_audio.doctor` | Not re-run as pass criteria |
| TypeSafe key via Settings | Not exercised |
| Real microphone Listen | Not exercised |

Operator checklist remains the hardening directive 37-step sequence via `npm run dev:desktop`.

## Installed NSIS smoke

| Check | Result |
| --- | --- |
| `npm run build:desktop` | Passed — `RELAY_0.1.0_x64-setup.exe` |
| Installer path (this build) | `C:\Users\maxwe\AppData\Local\Temp\cursor-sandbox-cache\fceeb6951bd0ffc4fa7c5f542dfa23a8\cargo-target\release\bundle\nsis\RELAY_0.1.0_x64-setup.exe` |
| Packaged `tools/audio` | Bundled via Tauri `bundle.resources` |
| Full install + Listen without repo | **Pending** operator |

## Operator prerequisites

```powershell
npm run readiness:windows
./dev/start-model.ps1 -StartHint
./dev/start-model.ps1
cd tools/audio
python -m pip install -e ".[live]"
python -m relay_audio.doctor
# place/download Whisper tiny.en before Listen
npm run dev:desktop
# Settings: store TypeSafe key; leave Allow hosted processing OFF initially
```

## Local model / audio / Jev

- Local model: external loopback (`external:ready` / `external:unavailable`)
- Audio: `source.ready` handshake; Replay does not require Listen or mic
- Jev: key ≠ disclosure; chip evidence-based (`configured` until successful request)

## Known remaining limitations

- Manual dogfood and installed-app Listen smoke still require operator hardware/services
- One production Reflex (`resolve-acronym@1`)
- No connectors / Halo / iPhone
- Local model not app-supervised beyond health checks

## Stop point

**STOP.** Final architecture/code review next. No broad cleanup until after that review.
