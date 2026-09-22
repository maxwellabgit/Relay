# G3 model delivery — not a green gate

Code SHA: `844c54444979d6413386e154779a29397f509eee`.

This slice does not green G3. No model is pinned. No download ran. No phone was used.

## What changed

- With no pin, delivery stays unselected and does not read bytes.
- A pin above 1.2 GB is rejected before any read.
- A matching hash can be installed, paused, cancelled, and deleted. A mismatched hash drops the bytes.
- Settings show download, pause, resume, cancel, and delete only after a pin leaves the unselected state. The app currently passes no pin.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 153 passed, architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

This is not a device tournament and not a headed Windows release profile.
