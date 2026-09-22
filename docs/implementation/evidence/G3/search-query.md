# G3 search query — not a green gate

Code SHA: `dfc2969ac246f878a0ad38522e23237569a35fb2`.

This slice does not green G3. No device tournament ran. No model is pinned.

## What changed

- A local search-query draft that is empty, too long, or more than one line is repaired once.
- A second invalid draft falls back to the Ask text.
- Memory and claim queries that are already explicit still skip the model.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 157 passed, architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

This is a lower-layer test. It is not a device model tournament.
