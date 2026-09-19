# STATUS

What has been verified, and how. Scripted evidence is never summarized as live evidence.

## Current decision

The additive Case/Jev prototype is frozen at tag `additive-prototype-94465cf` (`94465cf`). The alpha does not continue that migration. It ships one architecture:

```text
Sources → Cases → Judgments → Reflexes → Operations
```

Authoritative specs: `README.md`, `docs/FINAL_REFACTOR.md`, `docs/CONNECTORS.md`, `docs/REFLEXES.md`, `docs/PRIVACY.md`.

Superseded product documents live in `docs/archive/`.

## Phase

**Contracts defined but not wired.** Authority, disclosure, and versioned references are hardened. Alpha not complete.

`IRelayApplication`, `IConnector`, `IReflexHandler`, `ITranscriptSource`, `ConnectorCatalog`, and `ReflexCatalog` exist as inspectable contracts and catalogs. Approvals are hash- and case-version-bound. Observed content defaults to local-only disclosure. Production code still runs the frozen additive host. `Relay.Infrastructure` is a shell. No connector or built-in Reflex executes yet.

## Characterization only (not alpha gates)

- Graceful restart of the frozen prototype (`RestartCharacterizationTests`) — not process-death crash recovery.
- Plaintext object-store sentinel absence from one event payload (`PlaintextBoundaryCharacterizationTests`) — not encryption or full-store sentinel scan.
- Project-file and assembly reference edges (`ProductionDependencyTests`) — root `Relay.slnx` still temporarily includes legacy/Worker projects.
- Catalog cross-resolution and `OperationAuthority` binding tests — contract-level, not runtime broker coverage.

## Not yet proven

- One SQLite authority plus encrypted objects
- One background runtime with no UI pumping
- Wispr `ITranscriptSource` equivalent to text Ask
- Read-only connectors with per-action write toggles
- Four built-in Reflexes against real fixtures
- Legacy Session/Mind/Worker code removed from shipped binaries
- Packaged Windows dogfood with an empty problem report
- Production solution cutover (`Relay.Production.slnx` / `Relay.Legacy.slnx`)

## Alpha completion rule

A successful build is not completion. The packaged Windows app must pass the gates in `docs/FINAL_REFACTOR.md` on a fresh data root.
