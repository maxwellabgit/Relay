# G4 boot surfaces and internal-channel console — not a green gate

Code SHA: `5f83b2f2fd8a55e6d535ab246174bf6e8a8031e1`.

This slice does not green G4. No screenshot matrix, keyboard-only Windows pass, screen-reader pass, or device visual proof exists.

## What changed

- The product surface is `initializing`, `ready`, `busy`, `waiting`, `degraded`, `failed`, or `retrying`. Boot shows “Starting RELAY…” instead of an empty ready chat. A failed start shows “RELAY is not running.”
- Engine and storage failures are the only red core health. Jev, Halo, and other optional chips stay a warning.
- Machine codes on the consumer surface become short sentences. Raw codes stay in diagnostics.
- The thread follows the end only while the reader is already near the end.
- The developer console and acronym fixture replay open only when `EXPO_PUBLIC_RELAY_CHANNEL` is `internal` and `EXPO_PUBLIC_RELAY_DEV_CONSOLE` is `1` or `true`. A production channel, or a missing channel, stays hidden even if the flag is set.
- Metro swaps the demo client and the fixture module unless the channel is `internal` and the matching flag is on. Export scripts set `EXPO_PUBLIC_RELAY_CHANNEL=production` and unset the demo and console flags. The local demo launcher sets the channel to `internal`.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 136 passed, architecture 21 passed, integration 75 passed, replay 2 passed, privacy 5 passed. The architecture export scan ran `npx expo export` for web, iOS, and Android with the channel set to `production` and the demo and console flags removed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not visual, keyboard, screen-reader, or physical-device evidence.
