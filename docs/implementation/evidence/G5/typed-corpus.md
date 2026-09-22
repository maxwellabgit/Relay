# G5 typed corpus — not a green gate

Code SHA: `f94a4bb2f47acd0008e299b61af6569123aff104`.

This slice does not green G3 or G5. No device tournament ran. No runtime is pinned. No phone was used.

## What changed

- An invalid local candidate draft is repaired once. A second invalid draft stays invalid and does not fall back to a guessed kind.
- A cancelled or unavailable model falls back to the existing heuristic. An already aborted signal makes zero model calls.
- `LOCAL_TYPED_CORPUS` is a deterministic lower layer: commitment, open question, factual claim, and chatter.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 147 passed, architecture 21 passed, integration 76 passed, replay 2 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not a device model tournament and not a headed Windows release profile.
