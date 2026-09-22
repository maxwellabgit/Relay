# G3 mobile listening and diagnostics — not a green gate

Code SHA: `80f741b6a59e51690e2574f461bbb3cf9d4359b3`.

This slice does not green G3. No iPhone 15 Pro Max or Galaxy S23 Ultra run exists. The on-device model tournament and foreground speech package were not selected.

## What changed

- `SetListening(true)` stays off when the audio status is present and not ok. Turning listening off still succeeds.
- The phone shell no longer draws fixed listening bars. When audio is unavailable, the screen says listening stays off and shows the audio detail.
- The native mobile client writes traces through a bounded device file (200 events) instead of the browser trace route.
- Settings can share a redacted diagnostics document. It copies run, status, queue, cases, waits, decision, trace, and feed summaries. It does not copy source transcripts or saved memory text.
- Open run folder is passed only when the Tauri host is present.
- iOS and Android settings no longer show the Windows local-model address.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 130 passed, architecture 21 passed, integration 75 passed, replay 2 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not physical-device evidence.
