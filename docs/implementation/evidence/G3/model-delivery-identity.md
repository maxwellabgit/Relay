# G3 pinned model identity — not a green gate

Code SHA: `e370cfb14607bf7cb5712aa5664bd8274709bd22`.

This slice does not green G3. No model is pinned. No download ran. No device tournament was run.

## What changed

- When a pin exists, settings show size, a Wi-Fi hint, version, license, and the content hash.
- With no pin, download stays off and that block stays hidden.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
npx eslint packages/contracts/src/model-delivery.ts packages/ui/src/assistant/SettingsSheet.tsx
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 169 passed (44 files), architecture 21 passed, integration 76 passed, replay 3 passed, privacy 5 passed, eslint exit 0. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

This is a lower-layer copy check. It is not a device download.
