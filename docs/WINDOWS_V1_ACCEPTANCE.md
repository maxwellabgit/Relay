# Windows V1 Acceptance Record

## Baseline

- Branch: `harden/windows-v1-final`
- Baseline `origin/main` SHA: `ca54cde2ad11aa27392bf1bde99de2b682dd442d`
- Pre-hardening `verify:v1`: passed (with `prettier` `endOfLine: auto` for Windows CRLF)

## Hardening commits

1. `6626f8a` — `runtime: make case completion durable and recoverable`
2. `46b3d70` — `privacy: finish protected prose persistence boundary`
3. `56d5dfb` — `listen: make local transcription readiness truthful`
4. `ad0e224` — `authority: gate hosted Jev and expose runtime settings`
5. `8fa033b` — `diagnostics: close run lifecycle and case timing`
6. `63f270f` — `test: prove Windows V1 crash recovery boundaries`
7. (this commit) — `docs: record final Windows V1 hardening gate`

## Final automated gate

```text
npm run verify:v1
→ verify:v1 passed
```

Final tip: this commit on `harden/windows-v1-final` (`git rev-parse HEAD`).

## Manual Windows dogfood (Stage 7)

Status: **not complete on this machine** — required runtime services were unavailable.

| Prerequisite | Observed |
| --- | --- |
| Local llama.cpp-compatible server (`127.0.0.1:8080`) | Unavailable (`./dev/start-model.ps1` / `/v1/models` timeout) |
| Audio doctor | `python -m relay_audio.doctor` from `tools/audio`: `ok=false` — mic `PortAudioError`, Whisper `tiny.en` not present locally |
| TypeSafe key via Settings | Not exercised (no interactive Settings dogfood this run) |
| Real microphone Listen | Not exercised |

Operator must re-run the full 37-step dogfood from the hardening directive when model + ASR + mic + TypeSafe key are available via `npm run dev:desktop`.

## Installed NSIS smoke (Stage 8)

| Check | Result |
| --- | --- |
| `npm run build:desktop` | Passed — `RELAY_0.1.0_x64-setup.exe` produced |
| Packaged `tools/audio` resources | Present beside release binary under `tools/audio` (Tauri `bundle.resources`) |
| Full install + Listen without repo checkout | **Not completed** — silent install path not verified end-to-end in this session; requires operator install away from the source tree |

## Local model / audio / Jev (as configured)

- Local model: external loopback only (`external:ready` / `external:unavailable`); start hint via `./dev/start-model.ps1 -StartHint`
- Audio/ASR: `faster-whisper` optional live extra; readiness gated on `source.ready`
- Jev: TypeSafe credential separate from **Allow hosted processing** (default OFF)

## Known remaining limitations

- Manual 37-step dogfood and installed-app Listen smoke still require an operator machine with model + mic + Whisper weights + TypeSafe key
- Only one production Reflex (`resolve-acronym@1`)
- Connectors / Halo / iPhone remain out of scope
- Local model is not app-supervised beyond health checks

## Stop point

After this docs commit and a green `npm run verify:v1`, **stop**. Final review next — no Halo, iPhone, or broad cleanup until then.
