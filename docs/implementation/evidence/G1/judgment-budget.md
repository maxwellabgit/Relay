# G1 judgment budget — not a green gate

Code SHA: `5fc29eceea59e404159d0d9d36a45160e478b9f4`.

This slice does not green G1. The live canary was not run. No key was read or imported.

## What changed

- A judgment attempt records grant scope, expiry, request counts before and after, byte counts before and after, and the disclosed source count and byte total.
- A cache hit, a disclosure refusal, and a round-cap refusal do not spend the budget. A provider call does.
- The developer console shows those counts on the attempt. Source text stays out of the trace.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 155 passed, architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not a live canary.
