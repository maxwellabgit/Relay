# Windows V1 Acceptance Record

## Baseline

- Branch: `main`
- Wrap baseline SHA: `88d5437cb40de2941eb021d7d38b392664707579`
- Architecture authority: TypeScript engine + Tauri Windows adapters

## Wrap-up commits (on main)

1. `2cd857d` - `privacy: migrate legacy prose into protected artifacts`
2. `3899bf5` - `replay: decouple developer fixtures from live listening`
3. `b3a44a4` - `authority: fail closed on hosted judgment dispatch`
4. `6170013` - `authority: make Jev status evidence based`
5. `d55208e` - `docs: reconcile Windows V1 release-candidate state`
6. `6250c79` - `dev: add Windows V1 readiness gate`
7. `2e00c8f` - `test: wait for case completion in crash-recovery B/C/E/H`

## Freeze commits (on main)

1. `b0aeafa` - `fix: isolate Jev health from audio and model failures`
2. `fae977d` - `dev: distinguish code readiness from dogfood readiness`
3. `81eb0ec` / subsequent docs pins - `docs: freeze Windows V1 release-candidate record`
4. `03aa9a8` - `test: raise production-path acceptance timeout for CI`

**Final main SHA (CI-green tip):** `03aa9a851991b339f57ff8da4921b01b0aa1624c`

## Automated gates (freeze pass)

```text
npm run verify:v1
-> verify:v1 PASS

npm run readiness:windows
-> CODE READY / PASS

npm run readiness:dogfood
-> NOT READY FOR DOGFOOD on this machine (expected until model + audio doctor pass)
  - Local model unavailable
  - Microphone / Whisper may fail until operator setup

GitHub Actions check for 03aa9a851991b339f57ff8da4921b01b0aa1624c:
-> PASS (run 35550744256)
```

Prior wrap tip `2e00c8f` also had a green `check` run (`35548087632`).

## Manual Windows dogfood

Status: **pending** - not run on this machine.

| Prerequisite | Observed this session |
| --- | --- |
| Local llama.cpp (`127.0.0.1:8080`) | Unavailable |
| `python -m relay_audio.doctor` | Did not overall-pass (mic / Whisper) |
| TypeSafe key via Settings | Not exercised |
| Real microphone Listen | Not exercised |

Operator: `npm run readiness:dogfood` must PASS, then `npm run dev:desktop` and the freeze-pass Tests A-H.

## Installed NSIS smoke

| Check | Result |
| --- | --- |
| `npm run build:desktop` | Prior wrap build produced `RELAY_0.1.0_x64-setup.exe` |
| Installer path (prior build) | `C:\Users\maxwe\AppData\Local\Temp\cursor-sandbox-cache\fceeb6951bd0ffc4fa7c5f542dfa23a8\cargo-target\release\bundle\nsis\RELAY_0.1.0_x64-setup.exe` |
| Packaged `tools/audio` | Bundled via Tauri `bundle.resources` |
| Full install + Listen without repo | **Pending** operator |

Rebuild after freeze tip before claiming installed smoke on this SHA.

## Operator prerequisites

```powershell
npm run readiness:windows
npm run readiness:dogfood
./dev/start-model.ps1 -StartHint
./dev/start-model.ps1
cd tools/audio
python -m pip install -e ".[live]"
# place/download Whisper tiny.en before Listen (doctor uses local_files_only)
python -m relay_audio.doctor
npm run dev:desktop
# Settings: store TypeSafe key; leave Allow hosted processing OFF initially
```

## Local model / audio / Jev

- Local model: external loopback (`external:ready` / `external:unavailable`)
- Audio: Listen fails closed without `source.ready`; Replay is independent of Listen/audio health
- Jev: key != disclosure; chip evidence-based (`configured` until successful request); audio/model failures do not mark Jev degraded

## Known remaining limitations

- Manual dogfood and installed-app Listen smoke still require operator hardware/services
- One production Reflex (`resolve-acronym@1`)
- No connectors / Halo / iPhone
- Local model not app-supervised beyond health checks
- External Whisper model must already be present locally

## Stop point

**STOP** after freeze gates + operator dogfood/NSIS. Final architecture/code review next. No broad cleanup until after that review.
