# PRODUCTION CORE STATUS

Cross-run ledger for `cursor/relay-production-core`. Update only this file for phase state.

## Identity

| Field | Value |
| --- | --- |
| Branch | `cursor/relay-production-core` |
| Starting SHA | `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7` |
| Named history | `9fce11241019321d3d326c5b37f7578efc8d5630`, `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7` |
| Preservation tag | `relay-dotnet-a6bf987` (confirmed present) |
| Pre-existing user changes | none |
| Active phase | Phase 2 IN_PROGRESS |
| Last green gate | Phase 2 verify:v1 local PASS (diagnostics Bugbot fixes) |
| Phase 0 commit | `bc3689eadc1e166475bc8c1009f81b3f3842eacc` |
| Phase 1 commits | `1c3f666`, `af22805` |
| Phase 2 commit | `f0ad739` (+ pending Bugbot-fix commit) |

## Phase states

| Phase | State | Notes |
| --- | --- | --- |
| 0 Repair truthful baseline | GREEN | CI run https://github.com/maxwellabgit/Relay/actions/runs/35652324585 |
| 1 Remove retired architecture | GREEN | Bugbot pass; CI `35663319075` / `35663633131` |
| 2 Live correlated diagnostics | IN_PROGRESS | Bugbot fixes: live-summary publish, startedAt, harness isolation, heartbeat complete |
| 3 Decompose engine | NOT_STARTED | |
| 4 Tool and operation kernel | NOT_STARTED | |
| 5 Ambient triage / recommendations | NOT_STARTED | |
| 6 Claim verification + GitHub read | NOT_STARTED | |
| 7 Bounded Reflex creation | NOT_STARTED | |
| 8 Professional UI + Windows hardening | NOT_STARTED | |

## Next exact action

Commit Phase 2 Bugbot fixes, push, Bugbot PASS, confirm CI green, then start Phase 3.

## Evidence log

| When (UTC) | Command / event | Result |
| --- | --- | --- |
| 2026-09-21 | Phase 2 Bugbot findings fixed; `npm run verify:v1` | PASS (live-summary wire, startedAt preserve, harness latest isolation, heartbeat completed) |
| 2026-09-21 | Phase 0 GH Actions | PASS run `35652324585` (`ok smoke`, `ok desktop-build`, `verify:v1 passed`) on `bc3689e` |
| 2026-09-21 | `git rm` retired `src/` `tests/` `Relay.slnx` + obsolete scripts | Staged deletion; tag `relay-dotnet-a6bf987` retained |
| 2026-09-21 | Removed decorative tabs / dead UI panels | PhoneShell + packages/ui exports cleaned |
