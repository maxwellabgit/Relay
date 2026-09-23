# Release ledger reconciliation at `5631b2c`

Release recommendation: **NO-GO**.

This note corrects `docs/implementation/RELAY_V1_FINAL_RELEASE_WORKFLOW_31ffec5.md` at GitHub `5631b2c6d19175e338116c03cbd9fe3c766b4605`. It does not delete that file or rewrite its historical FAIL rows.

## What the older ledger got wrong

| Older statement | Correction at `5631b2c` |
| --- | --- |
| Nothing was pushed. Local `main` is ahead of `origin/main`. | `5631b2c` is on GitHub `main`. The push went directly to `main`. Reviewed-PR and required-check protection are still not in place. Do not change GitHub settings in the same pass as these code fixes. |
| Product evidence SHA `6e14487` or `485a0be`, not pushed. | The reviewed tip is `5631b2c6d19175e338116c03cbd9fe3c766b4605` (`test: import the staging key natively and record the live canary`). |
| Live canary is both HUMAN_BLOCKED and PASS for three HTTP 200s. | The Rust test is connectivity evidence only (`evidence: connectivity`). It does not prove grant charge, disclosure, the packaged receipt, or `healthy_live`. Those now go through `runPackagedLiveCanary` when `RELAY_LIVE_CANARY=1`. |
| Halo is a blocked V1 hardware capability and also disabled. | Halo is **not a V1 capability**. Desktop `halo_status` stays `disabled`. iOS Bluetooth usage strings are removed. Do not ship Halo until an official adapter exists. |
| EAS identifiers are a software leftover. | They remain placeholders. `npm run verify:eas-ids` is the account handoff after the code work. Do not invent Apple or Expo ids. |

## Historical results that stay

The combined Vitest failure at 22:14 on 2026-09-22, the privacy `EBUSY` row, G0–G8 readings that were NOT_RUN, and device/TestFlight blocks stay in the older ledger as the record of those runs. A later pass does not turn them green.

## Still NO-GO after the code in this tree

- Derived disclosure now requires parent artifacts. Desktop native attempts charge the physical grant. Headed `ok` is release passage, not runner completion. Public search keeps Wikipedia passages and does not treat suggestions as proof.
- `npm run verify:release-journeys` fails until every golden journey id is `PASS` in headed evidence.
- Mobile speech and model call native modules when linked. No on-device tournament winner is recorded. `npm run test:device:tournament` exits non-zero until it measures.
- EAS ids, branch protection, a signed installer, and physical Halo remain outside this code pass.
