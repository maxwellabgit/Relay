# VERIFICATION.md

Evidence levels: **deterministic** · **fixture** · **replay** · **live** · **windows-live**.
Scripted/fixture evidence is never summarized as live evidence.

## Baseline (pre-refactor)

| Field | Value |
| --- | --- |
| Recorded at (UTC) | 2026-09-17T19:57:12Z |
| Git SHA | `55551224b7a7fb5006f752d1015a9937aeea4f10` |
| .NET SDK | `10.0.401` |
| Host | Cursor Cloud Linux |
| Provider mode | n/a (no Jev calls in baseline) |
| `TYPESAFE_API_KEY` | absent |

### Commands and results

| Command | Result | Evidence |
| --- | --- | --- |
| `dotnet test tests/Relay.Core.Tests -c Release` | **Passed** 33/33 | deterministic |
| `dotnet build src/Relay.Gateway -c Release` | **Succeeded** 0 warnings | deterministic |
| `dotnet build src/Relay.Worker -c Release` | **Succeeded** 0 warnings | deterministic |
| `dotnet build src/Relay.DevHarness -c Release` | **Succeeded** 0 warnings | deterministic |

### Harness scenarios (distinct data roots under `/tmp/baseline-harness/{scenario}`)

| Scenario | Result | Evidence |
| --- | --- | --- |
| slice1 | PASS | deterministic |
| slice2 | PASS | deterministic |
| slice3 | PASS | deterministic |
| slice4 | PASS | deterministic |
| slice5 | PASS | deterministic |
| slice6 | PASS | deterministic |
| slice7 | PASS | deterministic |

### Known pre-existing / environment notes

| Item | Notes |
| --- | --- |
| `tests/Relay.Tests` (net10.0-windows) | Not part of §3 required baseline commands. On Linux, some tests fail due to Windows APIs (`kernel32` / exclusive file locks). Treat as environment, not new regressions, unless they appear on Windows CI. |
| WinUI Desktop build | Not runnable on this Linux host |
| Live Jev / local model | Not configured at baseline |

### Exit condition for §3

Baseline results are recorded. Subsequent failures can be distinguished from the green Core.Tests + slice1–7 baseline above.

## Post-refactor verification

| Gate | Status | Evidence level |
| --- | --- | --- |
| Persistence / async outbox tests (§4) | **Passed** — `AsyncPersistenceTests` 13/13; full `Relay.Core.Tests` 46/46 | deterministic |
| Provenance / grant tests (§5) | **Passed** — `ProvenanceGrantTests` 11/11 | deterministic |
| Jev transport fixture tests (§6) | pending | fixture |
| Decision catalog tests (§7) | pending | deterministic |
| Context / local jobs tests (§8) | pending | deterministic |
| Listening window tests (§9) | pending | deterministic |
| Workflow tests (§10) | pending | deterministic / fixture |
| Retention / capability tests (§11) | pending | deterministic |
| Desktop composition (§12) | pending | deterministic (headless); windows-live separate |
| `bash dev/verify-cloud.sh` (§13) | pending | deterministic + fixture |
| `bash dev/verify-live.sh` | blocked: missing credentials | live |
| `dev/verify-windows.ps1` | blocked: not Windows | windows-live |

### §4 notes

- `dotnet test tests/Relay.Core.Tests -c Release` → 46 passed (33 historical + 13 async/persistence).
- Intentional expectation change: cancel-before-dispatch no longer executes (see `docs/JEV-DECISIONS.md`).

### §5 notes

- Evidence artifacts carry restriction (`local_only` | `hosted_eligible`); `hosted_eligible` does not grant permission.
- Outbound package policy runs before hosted dispatch; `blocked_context_restriction` when required context cannot be exported.
- Budget reservations are atomic per transport attempt; estimates are never displayed as exact billed amounts.
