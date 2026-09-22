# G3 foreground speech session — not a green gate

Code SHA: `0be8c6fcb240616900291db0df5cab3a2ef5420a`.

This slice does not green G3. No iPhone 15 Pro Max or Galaxy S23 Ultra run exists. No Whisper package was selected, and no measured audio level was added.

## What changed

- A foreground speech session listens only after an explicit start reaches a ready source.
- Permission denial, interruption, route change, background, and cancel leave listening off and stop capture when a session was active.
- Engine start clears a stored listening flag, so a relaunch does not resume capture.
- The mobile client applies that relaunch decision from the speech port and applies the background suspend on host background.
- The desktop client applies the same suspend when capture dies or the host backgrounds. A `route_change` detail is recorded as a route change. Other source deaths are interruptions.
- The phone surface says why listening is off for those reasons.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 142 passed, architecture 21 passed, integration 76 passed, replay 2 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not a device speech pass.
