# G1 semantic rounds and one-time resume — not a green gate

Rounds and resume SHA: `ba54708e3590518621502fd028b597f9e845294e`.  
Parent grant command: `33251e563c09bf20d4a8b5173e2ca740cc92200b`.

This slice does not green G1. The live canary is still open.

## What changed

- One originating case can use two distinct Jev question sets. A repeat of the same question set does not consume another round. A third question set is refused before the provider call, and the terminal reason `max_semantic_rounds` is stored once.
- A missing key, authentication failure, disabled hosted processing, a refused disclosure grant, or an exhausted network/rate-limit/timeout retry becomes one durable wait. It is not dead-lettered and it does not invent an answer.
- Saving a valid session grant, or turning hosted processing on while that grant is active, resumes each parked judgment once. A second enable does not call the provider again.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
```

Recorded result: `tsc -b` exit 0, unit 126 passed, architecture 18 passed, integration 70 passed. Toolchain: Node v22.14.0, npm 10.9.7.
