# V1 Product Contract

Release authority companion to `docs/implementation/RELAY_V1_TestFlight_Finalization_Review_4b64928.md`.  
Executable matrix: `packages/contracts/src/capabilities.ts` (`V1_CAPABILITY_MATRIX`).

Marketing version: **1.0.0**  
Permanent application IDs: **`app.relay.assistant`** (iOS/Android), **`app.relay.desktop`** (Windows Tauri).  
Do not reuse NepTranslate identifiers, EAS projects, credentials, branding, or legal answers.

## Product promise

RELAY is a helpful local-first assistant and RELAY0 testing ground — not an always-on omniscient agent.

## Target recorded in Pass 1, not yet shipped

The September 23 product decisions are the target. They are not proof of implementation. See `docs/architecture/ADR-002-project-case-and-execution.md`.

- A ProjectCase is a durable folder with `## Case Intent`. An Execution is the per-input job previously stored as `CaseRecord`.
- Verify is the review inbox. Evidence status is not acceptance.
- After a connector is authorized, the user selects resources and Case bindings before content is retained. Observation of those resources is independent of microphone Listening.
- An activated Reflex may perform an explicitly scoped local action only after a fresh permission check and a durable receipt. Pass 1 proves that with a local Case edit. A provider write stays `not-shipped`.
- The deterministic calendar adapter is test-only. Google Calendar, Gmail, Slack, GitHub writes, mobile model/speech, hosted external-AI delegation, and physical Halo stay `not-shipped`. Pass 2 begins with P2.0 integrity repairs, then a live Calendar read. A daily gather picks one local time from 09:00 through 16:59 and records bound resource ids. It does not notify continuously and it does not draft mail or create Docs or Sheets.
- Consumer copy for deterministic results is a status line or one short question. That does not make the existing typed-ask path a shipped chat product.

The committed four-Reflex list below remains the reviewed module set. `docs/REFLEXES.md` also describes birthday, claim, and preserve-information designs that are not registered modules.

**RELAY turns repeated human judgment into software** through observable Cases, bounded Jev judgments, deterministic execution, and user-activated Reflexes.

## Included in V1

- Typed chat with responsive progress states and conversational continuity from local memory.
- Explicit **foreground** listening with visible timer, microphone indicator, stop control, interruption recovery, and text fallback.
- On-device small-model interpretation where the platform status is `shipped`, `degraded`, or `unverified-on-device`: classify intent, extract entities, draft bounded tool arguments, summarize grounded results, phrase concise answers.
- Jev for bounded semantic decisions only, behind explicit hosted-processing consent and narrow disclosure.
- Deterministic execution of local memory search, note capture, task/next-action capture, glossary/birthday memory, claim verification, and approved Reflex transitions — when the capability matrix marks them `shipped`, `degraded`, or `unverified-on-device` on that platform.
- Production public-search and GitHub read **only if** adapters, authorization, disclosures, citations, revocation, and receipts pass the same release gates. Otherwise they stay `not-shipped` and must be hidden from the app.
- Ambient triage that ignores noise and presents at most one useful recommendation at a time.
- Four reviewed Reflex modules (acronym, note, fact, next-action); no generated executable code.
- Calm consumer surface; separate developer surface derived from the same canonical events (desktop). Mobile production builds must not expose the developer drawer.

## Explicitly excluded from V1

- Always-on background microphone capture.
- Silent cloud fallback for local-model failure.
- Jev choosing arbitrary tools or authorizing actions.
- Automatic Reflex code generation.
- Calendar, email, financial, or other external writes without a real provider adapter and receipt.
- Claims of physical Halo support without device evidence.
- Ambiguous “wired” or “complete” status language — use only `shipped` | `degraded` | `unverified-on-device` | `not-shipped`.

## Authority split (immutable)

| Component | Responsibility |
| --- | --- |
| Deterministic runtime | Persistence, scheduling, source access, search, policy, permissions, budgets, idempotency, retries, execution, recovery |
| Local / mobile-tiny model | Conversation, summarization, extraction, drafting, bounded candidate queries |
| Jev | Fast typed judgments only (Choice / Noul / Score) |
| Connector | Auth + typed observe/read/write for one service |
| User | Connection scopes, hosted-processing grants, write enablement, operation approval, Reflex activation |

Models advise. Code authorizes and executes.

## Platform matrix rule

A feature may appear in production UI on a platform only when `capabilityStatus(id, platform)` is `shipped`, `degraded`, or `unverified-on-device`.  
`not-shipped` capabilities must be hidden. `unverified-on-device` means the code path exists and is not physical-device proof. Demo / testkit / recorded providers are never production proof.

## Composition rule

| Surface | Allowed client |
| --- | --- |
| Tauri desktop | `createDesktopClient()` |
| Native iOS / Android | `createMobileClient()` only (F2+). Until then, fail closed unless explicit demo flag. |
| Browser demo | `createBrowserDemoClient()` only when `EXPO_PUBLIC_RELAY_ALLOW_DEMO=1` |

Production compositions must not import `@relay/testkit` memory stores, recorded Jev, demo services, or fixture transports.

## Identity and versioning

| Field | Value |
| --- | --- |
| iOS bundle ID | `app.relay.assistant` |
| Android applicationId | `app.relay.assistant` |
| Desktop identifier | `app.relay.desktop` |
| Marketing version | `1.0.0` |
| iOS build number | EAS auto-increment |
| EAS project ID / ASC submit fields | Human-created RELAY records only (placeholders until F7/F8) |

## Status vocabulary

Never describe a capability as “wired.” Use the matrix statuses above and keep docs, UI, tests, and adapters aligned with `V1_CAPABILITY_MATRIX`.
