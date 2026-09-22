# G1 protocol slice — not a green gate

Protocol slice SHA: `8a5746b5143b0fff5f170d2079501b6117ea5002`.  
Parent truth freeze: `e281f2ce4bbbad17c4be0720dc6bd92839f12107`.

This slice does not green G1. The live canary, scoped disclosure grant, budget, two-round limit, and resume-once behavior are still open.

## What changed

- Production Jev model is the single constant `JEV_MODEL` (`jev-latest`). `typesafe` is not sent. A numeric `jev-x.y.z` value is accepted only as an explicit pin.
- Provider JSON is only `state`, `model`, and `questions`.
- Ambient state is `{ origin: "observed", excerpt }` for the candidate text. A neighboring string is not included.
- Acronym state includes the utterance excerpt, bounded to 400 characters.
- Responses fail closed on duplicate keys, unknown or missing outputs, wrong kinds, choice values outside the supplied options, and probability sums outside 0.02 of 1.
- `429` and `529` retry with exponential backoff and bounded jitter. A `Retry-After` delay is used when present, capped at 8 seconds. `401`, `402`, and `422` are not retried. Cancellation aborts the loop.
- Provider request id is kept from `x-request-id`, `request-id`, or the body `id` / `request_id` when the provider sends one.
- The desktop command performs one HTTP attempt and returns `retry_after` plus `request_id`. Retry timing stays in the TypeScript policy. This Rust file was not compiled here: Cargo 1.83 cannot parse an `edition2024` dependency in this environment.

## Tests

```text
npx vitest run --project unit packages/engine/src/typesafe-judgment.unit.test.ts packages/engine/src/ambient/provider-state.unit.test.ts packages/engine/src/judgments/acronym-state.unit.test.ts
npm run test:unit
npm run test:architecture
npx tsc -b --pretty false
```

Recorded result: unit 119 passed, architecture 18 passed, `tsc -b` exit 0. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.
