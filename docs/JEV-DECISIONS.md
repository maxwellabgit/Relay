# JEV-DECISIONS.md

Migration decisions made while implementing the Jev controller refactor.
Update this file whenever a default is chosen or an old test expectation is intentionally changed.

## Branch naming

- Spec requires `refactor/jev-runtime`. That exact name is used.
- Cloud agent branch prefix conventions are overridden by the explicit specification.

## SDK

- Pin `10.0.401` in `global.json` with `rollForward: disable` to match the Cloud Agent SDK used for baseline.

## Provider credentials

- `TYPESAFE_API_KEY` was **absent** at baseline. Live Jev transport tests will fail preflight until the key is provided.
- Fixture and replay clients require no key.

## Production vs historical mind

- `ICaseMind` / scripted minds remain for historical slice1–7 harness scenarios until replacement coverage lands.
- New production path uses `ICaseController` only.
- Harness will gain a composition factory; fixture provider mode is required for deterministic cloud gates.

## Cancellation semantics (intentional correction)

- Spec requires: never dispatch after cancellation; accept late results for audit without reopening the case.
- If an older test expected dispatch-after-cancel for “result capture,” replace that expectation and note it here.

## Open questions for humans

1. Confirm `refactor/jev-runtime` branch name is acceptable for merge to `main`.
2. When will `TYPESAFE_API_KEY` be available in Cursor Cloud secrets?
3. Local model endpoint for live jobs (host/port/model id)?
4. Should WinUI Desktop binding wait for a Windows verification machine, or ship Core+harness first and stub Desktop composition?

## Decision log

| Date (UTC) | Decision | Rationale |
| --- | --- | --- |
| 2026-09-17 | Start at SHA `5555122`; no intervening diff | Matched inspected baseline |
| 2026-09-17 | Create branch `refactor/jev-runtime` | Spec §3 requirement |
| 2026-09-17 | Proceed fixture-only until TypeSafe key arrives | Key absent; live gates must fail preflight |
| 2026-09-17 | Keep historical `ICaseMind` tests until controller parity | Spec: retain temporarily for historical tests |
