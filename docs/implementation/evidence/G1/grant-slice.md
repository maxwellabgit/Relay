# G1 disclosure grant slice — not a green gate

Parent protocol slice: `8a5746b5143b0fff5f170d2079501b6117ea5002`.

This slice does not green G1. Still open: a production command that creates a session or project grant, two semantic rounds, resume-exactly-once, and the live canary.

## What changed

- A hosted judgment needs a durable disclosure grant in addition to the hosted-processing switch. The switch remains a master off. Turning it on does not authorize a provider call.
- Grants are session or project scoped, expiring, revocable, and bounded by request count and bytes. Events are `jev.disclosure_granted`, `jev.disclosure_revoked`, and `jev.disclosure_consumed`.
- The envelope strips neighboring prose and unauthorized excerpt fields, then adds only the authorized source text. Local-only sources and revealing derivatives are refused before the provider call and before the judgment cache.
- Safe request artifacts record the grant id and disclosed source class, sha256, and byte count. They do not record source text.
- Recorded Node harnesses seed an explicit session grant. Desktop and mobile do not.
- The crash-recovery retry payload keeps the same 400-character acronym excerpt, so a completed judgment stays cacheable.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
```

Recorded result: `tsc -b` exit 0, unit 123 passed, architecture 18 passed, integration 69 passed. Toolchain: Node v22.14.0, npm 10.9.7.
