# G4 thread offset and in-flight send — not a green gate

Code SHA: `18e35cb9dc2d89315e45cf748a64e6b09c2f9199`.

This slice does not green G4. No screenshot matrix, keyboard-only pass, or screen-reader pass was run.

## What changed

- The app holds the thread offset. A remount of the thread follows the end only when the reader was already near it. Otherwise it restores the saved offset.
- The app holds the in-flight send. A remount of the input does not start a second send.
- Callers that omit those props keep the previous local behavior.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
npx eslint packages/ui/src/assistant/Composer.tsx packages/ui/src/phone/PhoneShell.tsx packages/ui/src/RelayWorkbench.tsx apps/relay/src/App.tsx packages/ui/src/phone/thread-scroll.ts packages/ui/src/index.ts
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 163 passed (42 files), architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed, eslint exit 0. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not a visual, keyboard, or device proof.
