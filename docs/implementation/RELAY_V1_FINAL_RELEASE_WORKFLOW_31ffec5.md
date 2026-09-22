# RELAY V1 final release ledger

Release recommendation: **NO-GO**.

G0–G8 keep the names and exit definitions in `docs/implementation/RELAY_LIVE_JEV_TESTFLIGHT_FINAL_WORKFLOW_1862daa.md`. This ledger does not redefine them. Evidence below is a software slice. It does not turn a gate green.

## Baseline reconciliation

| Item | Result |
| --- | --- |
| Worktree before edits | Clean on local `main` |
| Local `HEAD` before fetch | `05997c1`, ancestor of `origin/main` (87 commits behind) |
| Reconciliation | `git fetch --all --prune`, then `git merge --ff-only origin/main` |
| Expected SHA | `31ffec5ea5539525f2ff239d1b41d75564b2486e` |
| `HEAD` after fast-forward | `31ffec5ea5539525f2ff239d1b41d75564b2486e` on `main` |
| `origin/main` | same SHA |
| `origin/cursor/live-jev-g0-truth-45e9` | same SHA |
| `origin/cursor/v1-testflight-finalization-45e9` | `1862daa`, ancestor of `main` (0 unique commits). Historical. Not the release tip. |

Lower-layer counts reported for `31ffec5` (tsc exit 0, unit 169, architecture 21, integration 76, replay 3, privacy 5) are baseline evidence only. They are not a V1 pass.

`npm install` after the fast-forward linked five workspace packages that were missing from `node_modules` (`@relay/sqlite-core` and related). The lockfile did not change.

## Code tip this ledger describes

Product evidence SHA: `57d9a012a5ede76c6317d7e91bf15486afbe61f0` on local `main`. Not pushed.

Ledger introduction SHA: `13de8d77f0b6f7f849aa598398c6335330647535`.

| SHA | Commit |
| --- | --- |
| `25d9e4da1aa7fef524a99566cc0c6c9962d9267a` | fix: derive Jev disclosure from sealed provenance and charge each provider attempt |
| `3626dfa815ade6ad52f12a0a7cea08e2e0989072` | fix: sync the desktop lockfile with the 1.0.0 crate version |
| `57d9a012a5ede76c6317d7e91bf15486afbe61f0` | fix: seal authorized ambient transcripts before Jev can see them |

## Findings and decisions

1. Disclosure authority is the artifact provenance seal. Caller booleans `localOnly` and `revealsLocalOnly` are gone. The first seal wins. A later seal may only tighten policy. The same content hash cannot be resealed as more permissive, including under a new artifact id.
2. Missing provenance fails closed as `local_only`.
3. A microphone segment is sealed `hosted_session` only when hosted processing is already on and the session grant allows `ambient_transcript`. That happens at the first write. Jev then receives that excerpt and not a caller-supplied eligibility flag.
4. If the first seal is `local_only`, ambient routing stays on device. The provider is not called. Turning hosted processing on later does not upgrade those bytes.
5. Typed asks are still sealed `local_only` at ingest. G1 item 9 (authorized conversational context for acronym resolution) is not done.
6. Grant balances live in SQLite tables `hosted_grants` and `hosted_grant_reservations` (migration 12), not a bounded `domain_events` scan. A reservation is taken before a physical request, committed when the attempt starts, and released if no request was sent. Reopening the database releases reservations still in `reserved`. Committed attempts remain.
7. The TypeSafe transport owns retries. The judgment service does not retry the same logical call. Each physical HTTP request is one request unit. Per-attempt deadlines use `AbortController`. Missing key returns before the network and consumes no budget.
8. Health states are `unconfigured`, `configured_not_tested`, `healthy_live`, `degraded`, `unavailable`, and `disabled`. `ok` is true only for `healthy_live`. Ordinary judgment success does not call `noteLiveCanary()`. No live canary has been run. The user's key was not requested.
9. `cargo check --offline` for `apps/desktop/src-tauri` finished the dev profile successfully after `grants.rs` was added.
10. Architecture export purity did not run. `execFile("npx")` on this Windows host returns `spawn npx ENOENT` because `npx` is `npx.cmd`. That is a harness failure, not a bundle-purity result.
11. `artifacts/` is gitignored. `artifacts/release/57d9a012a5ede76c6317d7e91bf15486afbe61f0/release-result.json` is local evidence and is not committed.

## Phase checklist

| Phase | Status | Note |
| --- | --- | --- |
| 1 Jev trust boundary | PASS for the automated slice below | Live canary is HUMAN_BLOCKED until the native key import in Phase 2 exists and the user imports a key. G1 is not green. |
| 2 Jev secret provisioning | Windows import PASS; iOS NOT_RUN | Native staging-file import, DPAPI replace, reread, delete, and malformed rejection passed. The key is not a Tauri command argument. iOS Keychain is not implemented. |
| 3 Actions and tool execution | NOT_RUN | `recordedHarnessGrant` is still in `@relay/engine`. |
| 4 Windows product and installer | NOT_RUN | |
| 5 Native mobile runtime | NOT_RUN | Device tournament is DEVICE_BLOCKED until a development build is on the iPhone 15 Pro Max and Galaxy S23 Ultra. |
| 6 Halo adapter | NOT_RUN | Physical Halo is DEVICE_BLOCKED until hardware is attached. Do not claim the emulator mock as hardware. |
| 7 Headed journeys | NOT_RUN | |
| 8 UI polish | NOT_RUN | |
| 9 Cleanup, CI, release automation | NOT_RUN | Architecture export spawn is an open FAIL. Branch protection was not changed. |
| 10 Live canary and rehearsals | HUMAN_BLOCKED | Do not paste a key into Cursor. |
| 11 TestFlight | HUMAN_BLOCKED | No Expo/Apple authentication, credential creation, or submission. |

## Commands and results

Host: Windows `win32 10.0.26200`, local workstation. Times are local (UTC-4) on 2026-09-22.

| Command | When | Exit | Result | Evidence |
| --- | --- | --- | --- | --- |
| `npx tsc -b --pretty false` | 18:11 | 0 | PASS | no diagnostics |
| `npx vitest run --project unit --project integration --reporter=dot` | 18:11:04 | 0 | PASS, 61 files, 254 tests | unit 177, integration 77 (includes SQLite grant restart) |
| `npx vitest run --project replay --project privacy --project architecture --reporter=dot` | 18:11:43 | 1 | replay and privacy PASS; architecture FAIL | 28 passed, 1 failed |
| `cargo check --manifest-path apps/desktop/src-tauri/Cargo.toml --offline` | before `25d9e4d` | 0 | PASS, `Finished dev profile` in 42.62s | compiler output, not saved as a file |
| `cargo test --manifest-path apps/desktop/src-tauri/Cargo.toml --offline --lib secrets::tests -- --test-threads=1` | 18:16 | 0 | PASS, 4 tests | import, replace, reread, removal, malformed input, repository path rejection |

Architecture failure:

`tools/verification/production-bundle-graph.architecture.test.ts` › `keeps fixtures out of web, iOS, and Android exports when dev flags are off`

`Error: spawn npx ENOENT`

The export did not run, so bundle purity is **FAIL**, not PASS.

Phase 1 cases covered by `packages/engine/src/disclosure/jev-trust-boundary.unit.test.ts` and `adapters/node/src/hosted-grant-restart.integration.test.ts`:

- local-only disclosure rejection, including a later attempt to relabel the same bytes
- derived content inheriting `local_only`
- missing key with zero reservations
- cancellation before fetch
- per-attempt timeout
- 429 with Retry-After, then 500, then success, with the physical attempt count
- malformed JSON, duplicate keys, fractional score, and a `tool_call` key rejected
- concurrent reservations against `maxRequests: 1`
- process restart: uncommitted reservation released, committed attempt kept, next reserve can exhaust the grant
- ambient integration: authorized microphone excerpt is the provider `state.excerpt`; a segment sealed while hosted processing is off is not sent after hosted processing is enabled

## Gate reading

| Gate | Reading |
| --- | --- |
| G0 | NOT_RUN. Status documents still describe `1862daa`. This ledger is the new canonical file and does not by itself freeze every status doc. |
| G1 | NOT green. Automated disclosure, budget, retry, and response checks above passed on `57d9a01`. Live canary, key import, and authorized ask context are open. |
| G2–G8 | NOT_RUN |

## Remaining blockers

HUMAN_BLOCKED:

- Create `%LOCALAPPDATA%\Temp\relay-jev-key.txt` with one key line only after Phase 2 import exists. Do not paste the key into Cursor.
- Import through the native RELAY UI, confirm the live canary, and confirm the plaintext file was deleted.
- Apple Developer membership, App Store Connect app, Expo account, 2FA, signing, and the TestFlight submission authorization.
- Paid agreements and export-compliance answers.

DEVICE_BLOCKED:

- iPhone 15 Pro Max model, speech, and TestFlight acceptance.
- Galaxy S23 Ultra model and speech.
- Physical Brilliant Halo, until an official adapter exists. Until then the shipping Halo feature stays unclaimed.

FAIL:

- Production export purity did not execute (`spawn npx ENOENT`).

## TestFlight

Do not run `eas build` or submit. Phase 11 has not configured the real Expo project, bundle id, or production profile. When those software steps are done, the submission command remains unauthorized until the human actions in the release brief are complete.

## Rollback

Local `main` is ahead of `origin/main`. Nothing was pushed. To return this machine to the fetched baseline without a force push, create a new commit that reverts `57d9a01`, `3626dfa`, and `25d9e4d` in that order (`git revert` of those three, newest first). Do not reset. Remote `main` is still `31ffec5ea5539525f2ff239d1b41d75564b2486e`.
