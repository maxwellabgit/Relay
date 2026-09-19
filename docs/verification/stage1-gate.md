# Stage 1 gate — Tauri / Expo workbench

| Field | Value |
| --- | --- |
| Branch | `refactor/cross-platform-v1` |
| Baseline tag | `relay-dotnet-a6bf987` |
| Recorded | 2026-09-19 |

## Automated gate

```powershell
npm ci
npm run check
npm run test:unit
npm run test:integration
npm run test:replay
npm run test:privacy
npm run build:web
npm run build:desktop
```

Results on this machine:

| Command | Result |
| --- | --- |
| `npm ci` | ok |
| `npm run check` | ok (format, lint, typecheck, unit, architecture) |
| `npm run test:unit` | 17 passed |
| `npm run test:integration` | 2 passed |
| `npm run test:replay` | 2 passed |
| `npm run test:privacy` | 1 passed |
| `npm run build:web` | ok (`apps/relay/dist`) |
| `npm run build:desktop` | ok (NSIS `RELAY_0.1.0_x64-setup.exe`) |

## Manual acceptance (operator)

1. `npm run dev:desktop` or `./dev/run-relay.ps1`
2. Turn Listen on
3. Replay `fixtures/public/transcripts/acronym-basic.jsonl` at speed 1 and 0
4. Confirm Ask does not stop Listen
5. Confirm `runs/<run-id>/events.jsonl` grows without raw transcript
6. Restart and confirm completed work is not repeated

WinUI baseline launcher preserved as `./dev/run-dotnet-baseline.ps1`. `Relay.Desktop` removed from `Relay.slnx`.
