# PRODUCTION CORE DECISIONS

Durable architecture decisions for the production-core branch. Prefer amending this file over inventing competing plans.

## ADR-PC-001 — Verification single source of truth

- **Decision:** One executable manifest (`tools/verification/manifest.mjs`) defines the ordered V1 gate steps. Both `npm run verify:v1` and `.github/workflows/check.yml` consume it.
- **Why:** Duplicated step lists allowed `test:smoke` to disappear from `package.json` while CI and verify still invoked it, skipping `build:desktop`.
- **Consequence:** Adding/removing a gate requires editing the manifest (and any architecture integrity test), not two places.

## ADR-PC-002 — MSRP preflight vs smoke vs headed E2E

- **Decision:** Keep three distinct commands:
  - `test:smoke` — Vitest Node composition smoke (`production-smoke.integration.test.ts`)
  - `test:manual:msrp` — Node-harness deterministic preflight (not desktop E2E)
  - `test:e2e:msrp` — Headed Tauri desktop journey with isolated profile
- **Why:** Calling the Node harness “desktop E2E” falsely claims UI/DPAPI/composition coverage.

## ADR-PC-003 — Authority model (restated)

- Deterministic TypeScript owns state, permissions, execution, persistence, and side effects.
- Jev returns only typed Choice / Noul / Score over code-supplied options.
- Local model drafts language only; never authorizes or executes effects.
- User owns disclosure, connection scope, write enablement, operation approval, Reflex activation.
- Listening and hosted processing remain separate permissions.

## ADR-PC-004 — Execution Timeline naming

- **Decision:** Consumer/developer UI must not label deterministic paths as a “Jev Decision Tree.”
- **Replace with:** “Execution Timeline.” Jev detail renders only when canonical judgment events exist.
- **Preserve:** `05997c1` Ask text hydration from feed items.
