# G4 composer draft lift — not a green gate

Code SHA: `4634fff003136a2e75c618756cfcf2c525ba2b18`.

This slice does not green G4. No screenshot matrix, keyboard-only pass, or screen-reader pass was run.

## What changed

- The app holds the composer text. A remount of the input keeps text that has not been accepted.
- Callers that omit the value still keep text inside the input.
- A refused send still leaves the draft. An accepted send still clears only that same text.

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

These are lower-layer tests. They are not a visual, keyboard, or device proof.
