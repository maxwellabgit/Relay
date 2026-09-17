# RELAY local testing ground

Scripts and profiles for isolated Windows (and Core harness) local runs.

## Layout

```text
dev/
── bootstrap.ps1          One-time / per-machine prep
── start-model.ps1        Health-check / start local llama.cpp (Ministral)
── run-relay.ps1          Isolated data root + run manifest + diagnostics
── run-harness.ps1        Cross-platform Slice scenarios via Relay.DevHarness
── tail-relay.ps1         Tail runtime.jsonl for the current run
── replay-case.ps1        Replay a case event stream
── stop-relay.ps1         Stop the current run cleanly
└── profiles/
    └── local-ministral.json
```

## Run directory

```text
{data-root}/.dev-runs/{run-id}/
── manifest.json
── runtime.jsonl
── model.jsonl
── ui.jsonl
── payloads/          (only when preserveFocusedPayloads is true in the profile)
── screenshots/
── crash/
└── summary.json
```

## Linux / Cloud Agent

`Relay.Desktop` cannot run here. Use:

```powershell
./dev/run-harness.ps1 -DataRoot ./.dev-data -Scenario slice1
```

Or:

```bash
dotnet run --project src/Relay.DevHarness -- --data-root ./.dev-data --run-id slice1
```
