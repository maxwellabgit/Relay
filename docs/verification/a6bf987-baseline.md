# Verification: `a6bf987` C# / WinUI baseline freeze

| Field | Value |
| --- | --- |
| Commit | `a6bf987e5e4697412417dd05d7ab54e7eeddcd9d` |
| Tag | `relay-dotnet-a6bf987` |
| Subject | `core: harden authority disclosure and versioned references` |
| Branch | `refactor/cross-platform-v1` (created from `a6bf987`) |
| Recorded | 2026-09-19T09:48:46-04:00 |
| Environment | Windows 10.0.26200; SDK 10.0.400; runtime host 10.0.11 x64 |

## Exact commands

```powershell
git fetch origin
git switch -c refactor/cross-platform-v1 a6bf987
git tag -a relay-dotnet-a6bf987 a6bf987 -m "Final C# and WinUI baseline"

dotnet build Relay.slnx -c Release
dotnet test tests/Relay.Core.Tests/Relay.Core.Tests.csproj -c Release
```

## Results

| Command | Result |
| --- | --- |
| `dotnet build Relay.slnx -c Release` | succeeded (0 errors) |
| `dotnet test tests/Relay.Core.Tests/Relay.Core.Tests.csproj -c Release` | 168 passed, 2 failed, 0 skipped (170 total) |

## Known pre-existing failures

These failures match `docs/baseline/authority-contracts-harden.md` and also fail on parent `ac34ea1`. They are recorded here; they are **not** repaired as part of this freeze.

1. `Slice3ListeningTests.Raise_task_while_direct_case_also_active` — expected origin `direct`, actual `observed`
2. `Slice4ResearchTests.Lightshift_research_cites_stored_artifacts` — citation prefix `object:` vs expected `artifact:`

## Notes

* This freeze is the archive point for the C# / WinUI host. Git history and tag `relay-dotnet-a6bf987` are the archive; no `legacy/` tree is created.
* Subsequent Stage 1 work adds the TypeScript / Expo / Tauri workspace alongside this tree until the Stage 1 acceptance gate passes and WinUI is removed from the active solution.
