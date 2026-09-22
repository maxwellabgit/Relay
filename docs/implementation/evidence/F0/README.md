# F0 evidence

Phase: Reopen + make release truth enforceable  
Branch: `cursor/v1-testflight-finalization-45e9`

## Local gates (pre-push)

| Command | Result |
| --- | --- |
| `npm run format:check` | PASS |
| `npm run test:architecture` | PASS (18 tests) |
| `npm run test:unit` | PASS (92 tests) |
| `npm run test:e2e:golden` | PASS (28 integration lower-layer tests; journey 06 missing-harness by design) |

## Exact-tip CI

Recorded after push — see `FINALIZATION_STATUS.md` evidence log. GREEN requires Actions `head_sha` equal to this commit.

## Delivered

- Release authority review document
- Production-core reopened to FOUNDATION COMPLETE / V1 BLOCKED
- Finalization ledger with exact-SHA GREEN rule
- Golden journey manifest IDs 01–12 with honest proofKind
- Runner-safe `node tools/e2e/golden-journeys.mjs` (no nested tsx)
- Streaming `verify:v1` with always-written summary
- CI `timeout-minutes: 45` + verify summary artifact
