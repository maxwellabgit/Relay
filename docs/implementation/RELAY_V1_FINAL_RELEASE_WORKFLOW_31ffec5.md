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

Product evidence SHA: `6e1448790270a15fbc32933e16e8dc40741559ba` on local `main`. Not pushed. Earlier product evidence remains `485a0be894f06d69772ebb6cab46c12129269bef`. The 22:31 installer and journey 01 were built before `6e14487`.

Inspection at 22:19 local on 2026-09-22: `HEAD` is `c6514fa6978ba0da8e73a13db1bee4276a9ebc0c` on `main`. No node, cargo, or python test process was running. The privacy failure below was re-opened on that commit.

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
14. Jev choice labels are sealed into an artifact before the judgment work item is queued. The privacy project passed on the uncommitted tree. The change is not a new commit SHA.

## Phase checklist

| Phase | Status | Note |
| --- | --- | --- |
| 1 Jev trust boundary | PASS for the automated disclosure, retry, and response tests on `485a0be` | Live canary is HUMAN_BLOCKED. G1 is not green. |
| 2 Jev secret provisioning | Windows import remains on `3849e95`. iOS device-only flag is in source. Physical Keychain is DEVICE_BLOCKED | Do not paste a key into Cursor. |
| 3 Actions and tool execution | PASS for typed ambient acceptance, harness-grant move, and bundle purity | Wikipedia OpenSearch is injected. The tool still requires hosted processing and a public disclosure. Write effects still require acceptance. |
| 4 Windows product and installer | PASS for same-version install, in-place reinstall, launch, uninstall that keeps `%LOCALAPPDATA%\\RELAY`, a path with a space, and a per-user temp directory | Cross-version upgrade and a signed installer were not run. Signing is not configured. |
| 5 Native mobile runtime | NOT_RUN on device | In-memory assembly of large models is rejected. Tournament is DEVICE_BLOCKED. |
| 6 Halo adapter | Policy tests PASS. Shipping feature disabled | Official emulator NOT_RUN. Physical Halo DEVICE_BLOCKED. Do not claim hardware. |
| 7 Headed journeys | Journey 01 PASS. Typed note, fact, next action, model-unavailable, and relaunch PASS on the packaged app. Helpful chat NOT_RUN. Acronym choice HUMAN_BLOCKED | `npm run test:e2e:desktop` evidence under `artifacts/e2e/`. Journeys 03, 04, 06, 08, 10, 11, and 12 are still not headed product PASS. |
| 8 UI polish | NOT_RUN | No screenshot matrix. |
| 9 Cleanup, CI, release automation | `npm run verify:v1` PASS on `7f0fc475dbda3f27773789d1fb32e4aa393ff513` at 23:02 local, exit 0, 230881 ms | GitHub Actions did not run because nothing was pushed. Branch protection was not changed. |
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
- `adapters/node/src/protected-learning.privacy.test.ts` reported `EBUSY` on `state.sqlite` because `rmSync` in `finally` ran while the database was still open. The assertion that failed first was `expected true to be false` at the sqlite byte scan. The sentinel present in the file was `JEV_OPTION_SENTINEL_7f3a9c2e_UNIQUE`. Choice labels were stored in the queued work-item prompt.

Follow-up on the uncommitted tree at 22:26 local, parent `c6514fa6978ba0da8e73a13db1bee4276a9ebc0c`:

| Command | When | Exit | Result | Evidence |
| --- | --- | --- | --- | --- |
| `npx vitest run --project privacy adapters/node/src/protected-learning.privacy.test.ts --reporter=verbose` | 22:25 | 0 | PASS, 1 test | labels sealed before enqueue |
| `npx vitest run --project privacy --reporter=verbose` | 22:26 | 0 | PASS, 5 tests, 4 files | privacy project |
| `npm run build:desktop` | 22:28–22:31 | 0 | PASS, unsigned NSIS | `RELAY_1.0.0_x64-setup.exe` SHA-256 `B625B10773A3F879D448E93A0F34F76EE329A3B82CAF57C6D2D9DCF434604915` |
| `dev/nsis-install-smoke.ps1` with `CARGO_TARGET_DIR` set to the build output | 22:32 | 0 | PASS, silent install and 8s launch | `.dev-data/nsis-smoke-latest/result.json` |
| `dev/nsis-lifecycle.ps1` with the same `CARGO_TARGET_DIR` | 22:41 | 0 | PASS, same-version in-place install, launch, uninstall keeps user data, reinstall, path contains a space | `.dev-data/nsis-lifecycle-latest/result.json` |
| `npx tsx tools/e2e/msrp-desktop-headed.ts` against `relay-desktop.exe` from that build | 22:43 | 0 | PASS, journey 01 only | `.dev-data/e2e/msrp-headed-latest/result.json` |
| `npm run verify:secrets` | 22:43 | 0 | PASS | no pem, AWS, or sk- hits in source |
| `npm run verify:eas-ids` | 22:43 | 1 | HUMAN_BLOCKED | placeholder EAS project id and `REPLACE_WITH_*` Apple submit fields |
| `npx vitest run --project unit --project integration --project replay --project architecture --project privacy --reporter=dot` | 22:45 | 0 | PASS, 292 tests, 74 files | tree committed immediately afterward as `6e14487` |
| `npm run verify:v1` | 22:58–23:02 | 0 | PASS, 20 steps, 230881 ms | SHA `7f0fc475dbda3f27773789d1fb32e4aa393ff513`. Summary `.dev-data/verify/latest-summary.json` |

The queued clarification prompt now stores an artifact reference. `JudgmentService` loads the labels from that artifact. Inline `optionIds` still work for crash-recovery fixtures that already wrote a prompt. This fix is not committed. The combined unit/integration/replay/architecture/privacy command was not re-run, so that earlier FAIL row stays a historical result and is not rewritten as PASS.

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

- None open on the uncommitted privacy fix. The earlier combined Vitest command remains a historical FAIL because it was not re-run.

NOT_RUN:

- Cross-version Windows upgrade and a signed installer.
- Helpful local chat on a running local model. The packaged app showed the unavailable answer.
- Headed journeys 03, 04, 06, 08, 10, 11, and 12.
- GitHub Actions execution of `check` / `verify:v1`. The same command passed locally on `7f0fc47`.
- Official Halo emulator and physical Halo.
- On-device model tournament and speech.
- EAS project id, Apple id, ASC app id, and team id. `npm run verify:eas-ids` lists them. Do not invent values.
- The 22:45 combined Vitest run passed on the uncommitted tree. It is not an exact committed SHA yet.

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

## Next action

`npm run verify:v1` passed on `7f0fc475dbda3f27773789d1fb32e4aa393ff513`. Next independent work is the headed journeys that still have no packaged harness (03, 04, 06, 08, 10, 11, 12) and the official Halo emulator, which stays disabled until that integration is real. Do not push. Do not run `eas build`.

## Branch protection to require later

Do not change GitHub settings in this pass. When authorized, `main` should require a reviewed pull request and the `check` workflow job `test`, which runs `npm run verify:v1`. That command now includes format, lint, typecheck, unit, architecture, integration, replay, privacy, web/iOS/Android export, secret scan, Halo policy tests, Rust fmt/clippy/test, smoke, desktop build, and NSIS smoke.

## Rollback

Local `main` is ahead of `origin/main`. Nothing was pushed. To return this machine toward `31ffec5` without a force push, `git revert` the local commits newest first, including `485a0be`. Do not reset. Remote `main` was `31ffec5ea5539525f2ff239d1b41d75564b2486e` at the start of the earlier ledger.
