# QUESTIONS.md

Open items that need a human decision (not guessable from the repo):

1. ~~**Reconcile vs supersede `refactor/jev-runtime`**~~ — **Decided: B (supersede).** Continue from baseline `5555122`. Treat `origin/refactor/jev-runtime` as reference-only; do not merge its stack.
2. **Local model endpoint (Windows live gate)** — Set `RELAY_MODEL_ENDPOINT` (and enable model in Desktop settings / DPAPI) when ready to prove local generation. Default expected shape: OpenAI-compatible loopback URL + model name from settings.
3. **Wispr Flow** — Confirm a Wispr Flow capture path on the Windows verification machine; surface ingest is `IRelaySurface.IngestTranscript` while listening.
4. **TypeSafe API key** — Secret store name is `typesafe-jev` (DPAPI). For harness/live smoke set `TYPESAFE_API_KEY`; `jev-live` and `dev/run-live-gates.ps1` SKIP when absent.
5. ~~**Draft PR #1**~~ — Superseded by Phase 1 doc rewrite on main; close or ignore stale draft.
