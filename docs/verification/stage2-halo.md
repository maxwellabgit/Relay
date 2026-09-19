# Stage 2 — Halo emulator bridge

Official target: Brilliant SDK `halo_emulator` (not `frame-codebase` firmware).

## Components

* `tools/halo/relay_halo/bridge.py` — NDJSON stdin/stdout bridge with `EmulatorBrilliantMsg`
* `tools/halo/tests/` — listening/caption/finding/clear/disconnect/button/snapshot tests
* `devices/halo/lua/relay_plain_text.lua` — on-device TxPlainText contract
* `adapters/tauri/src/halo-display.ts` — TypeScript `GlassesDisplayPort`

## Run

```powershell
python -m pytest tools/halo/tests -q
npm run halo:emulator
```

Example frame:

```json
{"v":1,"type":"display.show","frame":{"kind":"finding","title":"API","body":"Application Programming Interface"}}
```

Do not put decision logs or full transcripts on the glasses.
