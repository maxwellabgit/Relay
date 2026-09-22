# G1 session grant command — not a green gate

Grant command SHA: `33251e563c09bf20d4a8b5173e2ca740cc92200b`.  
Parent grant enforcement: `b9e3247017a449ec1116b3961ea0757a12403367`.

This slice does not green G1. Still open: two semantic rounds, resume-exactly-once, and the live canary.

## What changed

- `GrantJevDisclosure` writes a session or project grant with an expiry, allowed source classes, and a request/byte budget. Limits are 1 minute to 24 hours, 1–50 requests, and 1–200,000 bytes.
- `RevokeJevDisclosure` revokes that grant by id.
- The snapshot `jevDisclosure` field shows the active session grant. A missing, revoked, or expired grant is empty.
- Settings can grant the current session for 12 hours, 20 requests, and 80,000 bytes, and can revoke it.
- Turning hosted processing on still does not create a grant. A harness can skip the recorded seed with `seedDisclosureGrant: false`.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
```

Recorded result: `tsc -b` exit 0, unit 124 passed, architecture 18 passed, integration 70 passed. Toolchain: Node v22.14.0, npm 10.9.7. The settings sheet was not opened in a browser; this environment has no running Expo web target. The grant and revoke path is covered by `hosted-processing.integration.test.ts`.
