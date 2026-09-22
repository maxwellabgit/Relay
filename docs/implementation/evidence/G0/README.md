# G0 evidence — freeze the truth

| Field | Value |
| --- | --- |
| Gate | G0 |
| G0 SHA | `e281f2ce4bbbad17c4be0720dc6bd92839f12107` |
| Parent baseline | `1862daacc8d06c6bc367c85b4cd523779d99b8fa` |
| Parent CI | https://github.com/maxwellabgit/Relay/actions/runs/35742750378 PASS (verify:v1 only) |
| Branch | `cursor/live-jev-g0-truth-45e9` |
| Recorded at | 2026-09-22 UTC |
| Node | `v22.14.0` |
| npm | `10.9.7` |
| rustc | `1.83.0 (90b35a623 2024-11-26)` |
| cargo | `1.83.0 (5ffbef321 2024-10-29)` |

G0 landed in `e281f2ce4bbbad17c4be0720dc6bd92839f12107`, a descendant of `1862daa`. This stamp commit only records that SHA. It does not green G1–G8 or F2–F8.

## What changed

- Added `docs/implementation/RELAY_LIVE_JEV_TESTFLIGHT_FINAL_WORKFLOW_1862daa.md`.
- Reconciled `docs/STATUS.md`, `docs/implementation/FINALIZATION_STATUS.md`, `docs/WINDOWS_V1_ACCEPTANCE.md`, and `docs/V1_PRODUCT_CONTRACT.md` to `1862daa`.
- Replaced the claim that remaining stops are human-only.
- Relabeled `V1_CAPABILITY_MATRIX` to `shipped` / `degraded` / `unverified-on-device` / `not-shipped`. No row is `shipped`.

## Focused test

```text
npx vitest run --project unit apps/relay/src/bootstrap/v1-capability.unit.test.ts
```

Result: exit 0, 12 tests passed, on parent `1862daa` before commit `e281f2c`.

## Still open

G1 is the next gate. Do not treat this document as live-Jev, headed-Windows, or physical-device evidence.
