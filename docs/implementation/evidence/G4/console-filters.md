# G4 developer-console filters — not a green gate

Code SHA: `d0cca3b84be83e9abf2322c0b7f2041af1b67341`.

This slice does not green G4. No screenshot matrix, keyboard-only Windows pass, screen-reader pass, or device visual proof exists.

## What changed

- Run events can be filtered by run, case, provider, and severity, in addition to stage, status, and reason.
- Provider is derived from the canonical stage or event type: `judgment.` or `jev.` is jev, `model.` is model, `halo.` is halo, and everything else is local.
- Severity is derived from status: failed, error, and blocked are error; waiting and degraded are warn; other statuses are info.
- Copy IDs shows the run, active case, decision, and session ids. Export selection writes JSON for the filtered rows. Neither includes transcript or memory text.
- Console buttons use a 44-point minimum height.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 139 passed, architecture 21 passed, integration 75 passed, replay 2 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not visual, keyboard, screen-reader, or physical-device evidence.
