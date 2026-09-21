# RELAY local testing ground

Scripts for isolated Windows TypeScript/Tauri local runs.

## Layout

```text
dev/
── start-model.ps1                   Health-check / start local llama.cpp
── run-relay.ps1                     Isolated run dir + `npm run dev:desktop`
── windows-v1-readiness.ps1          Structural CODE READY gate
── windows-v1-dogfood-readiness.ps1  Model + audio dogfood readiness
── tail-relay.ps1                    Tail current run diagnostics
── replay-case.ps1                   Replay a case event stream
── stop-relay.ps1                    Stop the current run cleanly
└── profiles/
    └── local-ministral.json
```

## Start the workbench

```powershell
npm run test:manual:msrp
./dev/run-relay.ps1
# or: npm run dev:desktop
```

The retired C# DevHarness / WinUI launchers were removed. Use tag `relay-dotnet-a6bf987`
if you need the archived baseline (`docs/archive/DOTNET_BASELINE.md`).
