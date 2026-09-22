# G3 direct answer — not a green gate

Code SHA: `fc37aa0bdb4826da2c9bad81ef8ee02b084fa2a5`.

This slice does not green G3. No device tournament ran. No model is pinned.

## What changed

- A local direct answer that is empty, too long, or a copy of the instructions is repaired once.
- A second invalid draft is not published as the reply.
- The Ask path and the respond tool share that check.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 160 passed, architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

This is a lower-layer test. It is not a device model tournament.
