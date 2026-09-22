# G3 pending transport removal — not a green gate

Code SHA: `c0850bb7cc58c016eb554489b0379385270c8c4f`.

This slice does not green G3. No phone was used. The live canary was not run.

## What changed

- `createExpoSystemOneTransport` is removed. It returned a pending stub even when a key was present, and nothing called it.
- The mobile client still creates judgments through `createTypeSafeJudgmentPort`.
- Production Expo and app sources no longer contain the pending stub name.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 148 passed, architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

The on-device model is still unselected. Real speech and both phones are still open.
