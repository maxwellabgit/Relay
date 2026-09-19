# Baseline at additive prototype plus alpha-boundary start

| Field | Value |
| --- | --- |
| Frozen prototype | `additive-prototype-94465cf` (`94465cf`) |
| Working tree parent | `94465cf5d1bdbfc678c9261c71542bf2e14ee903` |
| Environment | Windows; SDK 10.0.400 |
| Result | 15 passed, 0 failed |

Command (quoted filter required on PowerShell/CMD so `|` is not a pipeline):

```powershell
dotnet test tests/Relay.Core.Tests `
  --filter "FullyQualifiedName~PrivacySentinel|FullyQualifiedName~CrashPoint|FullyQualifiedName~ProductionDependency|FullyQualifiedName~ProductionComposition"
```

After the contract commit, rename filters to `PlaintextBoundary` / `RestartCharacterization` when re-running characterization.

Alpha is not complete. This manifest only records the characterization slice for commit 1 of `docs/FINAL_REFACTOR.md`. Those tests are graceful-restart and plaintext-boundary characterization, not alpha crash or encryption gates.
