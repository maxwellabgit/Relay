# G3 explicit short summary — not a green gate

Code SHA: `7daf729483315aba4e1ceb41ab56c7db8acf5e22`.

This slice does not green G3. No on-device model tournament was run. No speech package is pinned.

## What changed

- An Ask that starts with `summarize:` sends only the source text through the short-summary prompt.
- An empty summary, or one that repeats the instructions, is repaired once. A second invalid draft is not published.
- Any other Ask stays on the direct-answer path.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
npx eslint packages/engine/src/model/summarize.ts packages/engine/src/runtime/CaseRuntime.ts packages/engine/src/tools/builtins.ts packages/contracts/src/capabilities.ts
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 167 passed (43 files), architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed, eslint exit 0. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

This is a deterministic lower layer. It is not a device tournament.
