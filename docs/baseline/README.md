# Baseline characterization artifacts

Captured on branch `refactor/jev-decision-engine` from tag `pre-jev-refactor-55551224` (`5555122`).

| File | Contents |
| --- | --- |
| `build.txt` | `dotnet build Relay.slnx -c Debug` |
| `test.txt` | `dotnet test Relay.slnx -c Debug` (includes 2 known legacy failures) |
| `harness.txt` | DevHarness `slice1`–`slice7` summary payloads |

Harness run roots (gitignored): `.dev-data/scenarios/slice{N}/.dev-runs/slice{N}-baseline/`.
