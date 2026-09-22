# G2 semantics and bundle purity — not a green gate

Code SHA: `589598a0453c8db96fef923b1805422273549cb1`.

This slice does not green G2. The Windows installer was not built, and no headed product journey was run.

## What changed

- `App.tsx` no longer statically imports `@relay/testkit`. Fixture replay is a dynamic import behind `EXPO_PUBLIC_RELAY_DEV_CONSOLE`.
- Metro redirects that import, and the demo client import, to throw-only modules unless the matching flag is `1` or `true`. `export:web`, `export:ios`, and `export:android` clear both flags. Desktop `beforeBuildCommand` uses `export:web`.
- Explicit phrases store distinct memory kinds: note, fact, and recommendation. Feed copy is `Saved note: …`, `Remembered: …`, and `Next: …`. The library shows that prose.
- An explicit phrase is the approval. Acronym clarification does not swallow it. Ambient speech stages a review card and writes memory only after accept.
- Cancel during a local answer does not publish that answer. Repeating the same note keeps one memory. Replaying the same scripted segment does not create a second case.

## Tests

```text
npx tsc -b --pretty false
npm run test:unit
npm run test:architecture
npm run test:integration
npm run test:replay
npm run test:privacy
```

Recorded result on this machine before the code commit: `tsc -b` exit 0, unit 128 passed, architecture 21 passed (including web, iOS, and Android export scans), integration 74 passed, replay 2 passed, privacy 5 passed. Toolchain: Node v22.14.0, npm 10.9.7, rustc 1.83.0, cargo 1.83.0.

The architecture export test runs `npx expo export` for web, iOS, and Android with `EXPO_PUBLIC_RELAY_ALLOW_DEMO` and `EXPO_PUBLIC_RELAY_DEV_CONSOLE` removed. The web JavaScript and the iOS/Android Hermes bytecode did not contain `@relay/testkit`, `ACRONYM_BASIC_JSONL`, `MemoryEngineStore`, `MemoryArtifactStore`, or the acronym fixture sentence. Those exports are not a Tauri/NSIS installer and not a headed Windows run. Cargo 1.83 cannot compile the desktop crate in this environment.
