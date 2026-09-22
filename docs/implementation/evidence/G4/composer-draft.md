# G4 composer draft — not a green gate

Code SHA: `27aeabfae96fa0b60e8ec5968fca034e4a6658ab`.

This slice does not green G4. No screenshot matrix, keyboard-only pass, or screen-reader pass was run.

## What changed

- A send that returns false, or throws, leaves the composer text in place.
- The draft clears only when that same text is accepted. Text typed during the send stays.
- A second send is ignored while the first is still in flight.

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

These are lower-layer tests. They are not a visual, keyboard, or device proof.
