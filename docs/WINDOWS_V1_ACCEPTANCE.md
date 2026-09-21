# Windows V1 Acceptance Record

## Baseline

- Branch: `wrap/windows-v1-final`
- Wrap baseline SHA: `88d5437` (`docs: record final Windows V1 hardening gate`)
- Prior hardening lived on `harden/windows-v1-final` / related completion branches; architecture authority is TypeScript engine + Tauri Windows adapters.

## Wrap-up commits (this pass)

1. `privacy: migrate legacy prose into protected artifacts`
2. `replay: decouple developer fixtures from live listening`
3. `authority: fail closed on hosted judgment dispatch`
4. `authority: make Jev status evidence based`
5. `docs: reconcile Windows V1 release-candidate state`
6. `dev: add Windows V1 readiness gate`

Record SHAs with `git log --oneline 88d5437..HEAD` after the wrap commits land.

## Automated gates

```text
npm run verify:v1
→ required when claiming a full automated green; run on operator machine

npm run readiness:windows
→ diagnostic readiness (nonzero on required failures; no secrets printed)
```

This docs commit does **not** claim that `verify:v1` or full dogfood completed in the wrap session. Prefer recording exact command output when re-run.

## Manual Windows dogfood

Status: **not complete on this machine** — required runtime services may be unavailable.

| Prerequisite | Notes |
| --- | --- |
| Local llama.cpp-compatible server (`127.0.0.1:8080`) | Start via `./dev/start-model.ps1` |
| Audio doctor | `python -m relay_audio.doctor` from `tools/audio` |
| TypeSafe key via Settings | Interactive Settings dogfood |
| Real microphone Listen | Listening ON required for mic finals; Replay does not enable Listen |

Operator must re-run the full dogfood checklist when model + ASR + mic + TypeSafe key are available via `npm run dev:desktop`.

## Installed NSIS smoke

| Check | Result |
| --- | --- |
| `npm run build:desktop` | Produce `RELAY_*_x64-setup.exe` when packaging |
| Packaged `tools/audio` resources | Expected beside release binary under `tools/audio` (Tauri `bundle.resources`) |
| Full install + Listen without repo checkout | **Pending** — requires operator install away from the source tree |

## Local model / audio / Jev (as configured)

- Local model: external loopback only (`external:ready` / `external:unavailable`); start hint via `./dev/start-model.ps1 -StartHint`
- Audio/ASR: `faster-whisper` optional live extra; readiness gated on truthful source status
- Jev: TypeSafe credential separate from **Allow hosted processing** (default OFF); chip is evidence-based (`configured` until a successful request)

## Known remaining limitations

- Manual dogfood and installed-app Listen smoke still require an operator machine with model + mic + Whisper weights + TypeSafe key
- Only one production Reflex (`resolve-acronym@1`)
- Connectors / Halo / iPhone remain out of scope
- Local model is not app-supervised beyond health checks

## Stop point

After wrap commits and a green readiness/typecheck pass on the operator machine, **stop** for final review — no Halo, iPhone, or broad cleanup until then. Do not claim manual dogfood passed unless it was actually run.
