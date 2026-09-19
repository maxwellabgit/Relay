# Bounded expansion end-to-end

| Field | Value |
| --- | --- |
| Branch | `main` |
| Recorded | 2026-09-19 |
| Host | Windows, Node 22 |

## Commands

```powershell
npm run lint
npm run typecheck
npm run test:unit
npm run test:integration
npm run test:replay
npm run test:privacy
npm run test:architecture
npm run build:web
```

## Results

| Command | Result |
| --- | --- |
| `npm run lint` | ok |
| `npm run typecheck` | ok |
| `npm run test:unit` | 9 files, 27 passed |
| `npm run test:integration` | 2 files, 10 passed |
| `npm run test:replay` | 1 file, 2 passed |
| `npm run test:privacy` | 1 file, 1 passed |
| `npm run test:architecture` | 1 file, 1 passed |
| `npm run build:web` | ok. Expo web export wrote `apps/relay/dist` (469KB bundle) |

Integration coverage includes the engine Ask path and the bounded-expansion client scenarios: explicit glossary memory survives a SQLite restart, three unrelated unknowns do not become one candidate, three equivalent calendar-block episodes recommend a Reflex and stop at approval, an ambiguous acronym waits visibly when Jev is missing, and a passing Choice completes. The privacy run keeps the sentinel out of `events.jsonl`.

`npm run check` was not used as the gate. `prettier --check .` still fails on pre-existing JSON and config files that are not part of this change.

## Live workbench

`npx expo start --web --port 8093` rendered the workbench. The inspector showed run id, mode `live`, queue 0, Jev `missing key`, storage `memory`, and a `run.started` trace row. The servers on ports 8091, 8092, and 8093 were stopped after that check.
