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

**Final additive prototype frozen. Alpha refactor in progress. Alpha not complete.**

## Not yet proven

- One SQLite authority plus encrypted objects
- One background runtime with no UI pumping
- Wispr `ITranscriptSource` equivalent to text Ask
- Read-only connectors with per-action write toggles
- Four built-in Reflexes against real fixtures
- Legacy Session/Mind/Worker code removed from shipped binaries
- Packaged Windows dogfood with an empty problem report

## Alpha completion rule

A successful build is not completion. The packaged Windows app must pass the gates in `docs/FINAL_REFACTOR.md` on a fresh data root.
