# PRODUCTION CORE STATUS

Cross-run ledger for `cursor/relay-production-core` (foundation work).  
**V1 / TestFlight release authority is `RELAY_V1_TestFlight_Finalization_Review_4b64928.md` + `FINALIZATION_STATUS.md`.**

## Identity

| Field | Value |
| --- | --- |
| Branch | `cursor/relay-production-core` (merged to `main`) |
| Starting SHA | `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7` |
| Named history | `9fce11241019321d3d326c5b37f7578efc8d5630`, `05997c1defd3cecac6fb82ba9b4efc24f30ea6e7` |
| Preservation tag | `relay-dotnet-a6bf987` (confirmed present) |
| Pre-existing user changes | none |
| Active phase | **FOUNDATION COMPLETE / V1 BLOCKED** |
| Last green gate | Exact-tip GH Actions `35727102376` on `4b64928` (verify:v1 only) |
| Active blocker | V1 finalization F0–F8 incomplete — see FINALIZATION_STATUS.md |
| Tip SHA | `4b64928bedaeea6d601ec50ef18ad8fc03fc1bb6` |
| Implementation parent | `4a4529d1529adf6d6460e17e2797e02b16135182` |
| Phase 0–8 commits | See prior ledger rows; foundation work retained |

## Phase states (foundation)

| Phase | State | Notes |
| --- | --- | --- |
| 0 Repair truthful baseline | FOUNDATION GREEN | CI run https://github.com/maxwellabgit/Relay/actions/runs/35652324585 |
| 1 Remove retired architecture | FOUNDATION GREEN | Bugbot pass; CI `35663319075` / `35663633131` |
| 2 Live correlated diagnostics | FOUNDATION GREEN | Bugbot pass; CI on `a7e4208` |
| 3 Decompose engine | FOUNDATION GREEN | Bugbot pass (`66fa6f5`); desktop txn + exclusive store mutex |
| 4 Tool and operation kernel | FOUNDATION GREEN | Bugbot pass (`87c2695`); hosted gate + public-search connector |
| 5 Ambient triage / recommendations | FOUNDATION GREEN | Bugbot pass (`1abde49`) |
| 6 Claim verification + GitHub read | FOUNDATION GREEN | Bugbot pass (`057e358`) |
| 7 Bounded Reflex creation | FOUNDATION GREEN | Bugbot pass (`ecc8061`) |
| 8 Professional UI + Windows hardening | FOUNDATION GREEN | Headed MSRP + integration golden lower layer; **not** V1 product-complete |

## Why reopened

`4b64928` was a docs-only commit that declared production-core COMPLETE before its own exact-tip CI finished, citing parent SHA `4a4529d` / run `35684028985`. Exact-tip run `35727102376` later passed verify:v1, but headed journeys 2–12, mobile composition, on-device model, soak depth, and store readiness remain open blockers per the TestFlight finalization review. Foundation gains are preserved; **V1 is blocked**.

## Next exact action

Execute finalization phases F0–F8 per `FINALIZATION_STATUS.md`. Do not mark this ledger V1 COMPLETE.

## Evidence log

| When (UTC) | Command / event | Result |
| --- | --- | --- |
| 2026-09-22 | Exact-tip GH Actions verify:v1 on `4b64928` | PASS https://github.com/maxwellabgit/Relay/actions/runs/35727102376 |
| 2026-09-22 | Parent-SHA GH Actions on `4a4529d` | Historical PASS `35684028985` — not tip proof for `4b64928` claims |
| 2026-09-22 | Completion claim reopened → FOUNDATION COMPLETE / V1 BLOCKED | See finalization review |
| 2026-09-22 | Phase 8 Bugbot / local headed MSRP / NSIS process smoke | Historical foundation evidence only |
