# Verification: harden authority, disclosure, and versioned references

| Field | Value |
| --- | --- |
| Parent | `ac34ea113e1531bc5ff691ebc8bde923e8a4b3dc` (`ac34ea1`) |
| Commit subject | `core: harden authority disclosure and versioned references` |
| Recorded | 2026-09-19T08:16:03-04:00 |
| Environment | Windows 10.0.26200; SDK 10.0.400; runtime host 10.0.11 x64 |
| `git diff --check` | clean |
| `dotnet build Relay.slnx -c Release` | succeeded |
| Contract filter | 55 passed, 0 failed |
| Full `Relay.Core.Tests` Release | 168 passed, 2 failed, 0 skipped (170 total) |

## Exact commands

```powershell
git diff --check
dotnet test tests/Relay.Core.Tests/Relay.Core.Tests.csproj -c Release
dotnet build Relay.slnx -c Release
```

Contract filter used for the authority/catalog slice:

```powershell
dotnet test tests/Relay.Core.Tests/Relay.Core.Tests.csproj -c Release --filter "FullyQualifiedName~DataPolicy|FullyQualifiedName~OperationAuthority|FullyQualifiedName~ConnectorCatalog|FullyQualifiedName~ReflexCatalog|FullyQualifiedName~CrossCatalog|FullyQualifiedName~TelemetryRedactor|FullyQualifiedName~PlaintextBoundary|FullyQualifiedName~RestartCharacterization|FullyQualifiedName~ProductionDependency"
```

## Known pre-existing failures (also on `ac34ea1`)

These are not introduced by this contract commit; they fail on the parent commit with the same assertions:

- `Slice3ListeningTests.Raise_task_while_direct_case_also_active` — expected origin `direct`, actual `observed`
- `Slice4ResearchTests.Lightshift_research_cites_stored_artifacts` — citation prefix `object:` vs expected `artifact:`

## Notes

This manifest covers the authority/disclosure/versioned-reference hardening commit, not the frozen additive prototype (`docs/baseline/additive-prototype-94465cf.md`).
