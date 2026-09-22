# G1 judgment evidence — not a green gate

Code SHA: `d63412114b5aff11a3681638d0af487aa85bacc6`.

This slice does not green G1. The live canary was not run. No key was read or imported.

## What changed

- Canonical judgment events can carry a disclosure grant id, an HTTP status, a retry delay, and a provider request id.
- A request id is stored only when it is a short token. A missing id stays omitted. Prose is rejected.
- The developer console lists those fields on each judgment attempt. The redacted diagnostics snapshot includes the same trace fields.
- The live transport is unchanged. This does not call the provider.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 143 passed, architecture 21 passed, integration 76 passed, replay 2 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

These are lower-layer tests. They are not a live canary.
