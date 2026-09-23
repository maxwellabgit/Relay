# RELAY V1 final release ledger

Release recommendation: **NO-GO**.

G0–G8 keep the names and exit definitions in `docs/implementation/RELAY_LIVE_JEV_TESTFLIGHT_FINAL_WORKFLOW_1862daa.md`. This ledger does not redefine them. Nothing below turns a gate green.

## Baseline reconciliation

| Item | Result |
| --- | --- |
| Worktree before this pass | Clean on local `main` |
| Expected SHA | `31ffec5ea5539525f2ff239d1b41d75564b2486e` |
| `HEAD` at inspection | `3849e95177e8c9f76aeca9c890a128bbb0302667` on `main` |
| Expected branches present | local `main`; `origin/cursor/live-jev-g0-truth-45e9` exists. Local `main` is the working branch. |
| Reconciliation | Six commits sit on top of `31ffec5`. They were inspected and kept. They are not a reset. |

Commits between `31ffec5` and the pre-edit tip:

| SHA | Commit |
| --- | --- |
| `25d9e4d` | fix: derive Jev disclosure from sealed provenance and charge each provider attempt |
| `3626dfa` | fix: sync the desktop lockfile with the 1.0.0 crate version |
| `57d9a01` | fix: seal authorized ambient transcripts before Jev can see them |
| `13de8d7` | docs: record the Phase 1 release ledger at 57d9a01 |
| `a95e3d4` | docs: record the Phase 1 ledger introduction SHA |
| `3849e95` | fix: import the Jev key from a native staging file |

Lower-layer counts reported for `31ffec5` (tsc exit 0, unit 169, architecture 21, integration 76, replay 3, privacy 5) are baseline evidence only.

## Code tip this ledger describes

Product evidence SHA: `485a0be894f06d69772ebb6cab46c12129269bef` on local `main`. Not pushed.

| SHA | Commit |
| --- | --- |
| `485a0be894f06d69772ebb6cab46c12129269bef` | fix: disclose ask context only from its sealed artifact and prove export purity. |

## Findings and decisions

1. Typed asks are sealed with `conversation_excerpt` only when hosted processing is on and the session grant allows that class. Otherwise the seal stays `local_only`. A later put cannot widen it.
2. Acronym judgment discloses `contextExcerpt` only by citing the already sealed source artifact. It no longer writes a new artifact with a hosted policy. Missing provenance fails closed.
3. Accepting an ambient card stores a note for save, a recommendation for task and review, and no memory row for verify. Verify still opens an explicit claim check.
4. `recordedHarnessGrant` lives in `@relay/testkit`. `@relay/testkit` is a devDependency of `@relay/app`.
5. Web, iOS, and Android production exports were built on this host and did not contain the fixture needles. The previous `spawn npx ENOENT` failure is fixed by invoking the Expo CLI through `node`. Hermes bundles are scanned as bytes because Windows has no `strings` command.
6. Models larger than 32 MB are rejected unless a file sink is supplied. The sink path does not concatenate the download into one JavaScript buffer. `ModelDelivery(null)` remains the unselected state. No model was chosen.
7. Wikipedia OpenSearch is implemented and unit-tested. Production desktop and mobile clients still do not inject it. The capability stays `not-shipped`.
8. Consumer settings no longer show the grant id or byte counters. Those counters are on the developer console grant card.
9. iOS SecureStore writes use `WHEN_UNLOCKED_THIS_DEVICE_ONLY`. Physical Keychain proof was not run.
10. Halo shipping status is `disabled`. The Python policy adapter rejects transcript-like frames, clips text, dedupes, and applies backpressure, and it does not send when the official SDK is absent. `brilliant_msg` and `halo-emulator` were not installed. Physical Halo was not attached. The Rust command returns `disabled`.
11. The live Jev canary was not run. No key was requested.
12. Status documents that still name `1862daa` were not rewritten. This SHA is not a release candidate.
13. Branch protection was not changed. Nothing was pushed. `eas build` was not run.

## Phase checklist

| Phase | Status | Note |
| --- | --- | --- |
| 1 Jev trust boundary | PASS for the automated disclosure, retry, and response tests on `485a0be` | Live canary is HUMAN_BLOCKED. G1 is not green. |
| 2 Jev secret provisioning | Windows import remains on `3849e95`. iOS device-only flag is in source. Physical Keychain is DEVICE_BLOCKED | Do not paste a key into Cursor. |
| 3 Actions and tool execution | PASS for typed ambient acceptance, harness-grant move, and bundle purity | Public search stays hidden. Write effects still require acceptance. |
| 4 Windows product and installer | NOT_RUN for the installer lifecycle | Grant identifiers moved to the developer console. |
| 5 Native mobile runtime | NOT_RUN on device | In-memory assembly of large models is rejected. Tournament is DEVICE_BLOCKED. |
| 6 Halo adapter | Policy tests PASS. Shipping feature disabled | Official emulator NOT_RUN. Physical Halo DEVICE_BLOCKED. Do not claim hardware. |
| 7 Headed journeys | NOT_RUN | |
| 8 UI polish | NOT_RUN | No screenshot matrix. |
| 9 Cleanup, CI, release automation | NOT_RUN as a full CI expansion | Export purity now executes on this Windows host. Branch protection unchanged. |
| 10 Live canary and rehearsals | HUMAN_BLOCKED | |
| 11 TestFlight | HUMAN_BLOCKED | `REPLACE_WITH_*` remains in `eas.json`. No authentication or submission. |

## Commands and results

Host: Windows `win32 10.0.26200`, Node `v22.13.1`. Times are local (UTC-4) on 2026-09-22. Tree under test matches `485a0be`.

| Command | When | Exit | Result | Evidence |
| --- | --- | --- | --- | --- |
| `npx tsc -b --pretty false` | 22:04 | 0 | PASS | no diagnostics |
| `npx vitest run --project unit --project integration --project replay --project architecture --project privacy --reporter=dot` | 22:14 | 1 | 287 passed, 2 failed, 72 files | see failures below |
| `npx vitest run --project integration adapters/node/src/local-model.integration.test.ts` | 22:14 | 0 | PASS, 2 tests | isolated rerun |
| `npx vitest run --project architecture --testNamePattern "keeps fixtures out of web"` | 22:13 | 0 | PASS, web/iOS/Android export purity | 18.7s |
| `python -m pytest tools/halo/tests -q` | 22:03 | 0 | PASS, 5 tests | policy only; transport disabled |
| `npx eslint` on the edited TypeScript files | 22:14 | 1 | pre-existing `createDesktopClient.ts` `_value` unused | not introduced by this commit |

Failures in the combined Vitest run:

- `adapters/node/src/local-model.integration.test.ts` timed out at 2s while the machine was also exporting bundles. The same file passed alone immediately afterward. Not recorded as a product defect.
- `adapters/node/src/protected-learning.privacy.test.ts` reached `harness.close()` and then `rmSync` failed with `EBUSY` on `state.sqlite` inside Vitest. The same close-and-delete sequence succeeds under `tsx` outside Vitest. Privacy is **FAIL** for that cleanup, not PASS.

## Gate reading

| Gate | Reading |
| --- | --- |
| G0 | NOT_RUN. Status documents were not rewritten onto this SHA. |
| G1 | NOT green. Disclosure and export checks above passed. Live canary, physical key use, and headed proof are open. |
| G2 | NOT green. Bundle purity passed. Installer and headed journeys did not. |
| G3–G8 | NOT_RUN |

## Remaining blockers

HUMAN_BLOCKED:

- Create `%LOCALAPPDATA%\Temp\relay-jev-key.txt` with one key line. Do not paste the key into Cursor.
- Import it through the native RELAY UI, confirm the plaintext file was deleted, and run the live canary.
- Apple Developer membership, App Store Connect app, Expo account, 2FA, signing, and TestFlight submission authorization.

DEVICE_BLOCKED:

- iPhone 15 Pro Max model, speech, Keychain, and TestFlight acceptance.
- Galaxy S23 Ultra model and speech.
- Physical Brilliant Halo. The shipping Halo feature stays disabled until an official transport is connected.

FAIL:

- Vitest cleanup of `protected-learning.privacy.test.ts` hits `EBUSY` on this Windows host.

NOT_RUN:

- Windows installer lifecycle, headed journeys, official Halo emulator, model tournament, and EAS submission.

## TestFlight

Do not run `eas build` or submit. `apps/relay/eas.json` still contains `REPLACE_WITH_APPLE_ID`, `REPLACE_WITH_ASC_APP_ID`, and `REPLACE_WITH_TEAM_ID`. When the software steps and human authorization are done, the submission command remains:

`npx eas build --platform ios --profile production --auto-submit`

That command is not authorized.

## Key placement (only after the user chooses to dogfood)

1. Create `%LOCALAPPDATA%\Temp\relay-jev-key.txt` outside the repository.
2. Put exactly one non-empty key line in it.
3. Import it from RELAY Settings. Do not paste it into Cursor.
4. Confirm the file was deleted after import.
5. Remove and import again only when checking the key lifecycle.

## Rollback

Local `main` is ahead of `origin/main`. Nothing was pushed. To return this machine toward `31ffec5` without a force push, `git revert` the local commits newest first, including `485a0be`. Do not reset. Remote `main` was `31ffec5ea5539525f2ff239d1b41d75564b2486e` at the start of the earlier ledger.
