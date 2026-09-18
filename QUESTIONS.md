# QUESTIONS.md

Open items that need a human decision (not guessable from the repo):

1. ~~**Reconcile vs supersede `refactor/jev-runtime`**~~ — **Decided: B (supersede).** Continue from `refactor/jev-decision-engine` / baseline `5555122`. Treat `origin/refactor/jev-runtime` as reference-only; do not merge its stack.
2. **Local model endpoint (Windows live gate)** — Which host/port/model should live runners use (e.g. Ollama OpenAI-compatible URL + model name)?
3. **Wispr Flow** — Is a Wispr Flow capture path available on the Windows verification machine for listening/live gates?
4. **TypeSafe API key** — Confirm secret store name (`typesafe-jev`) and whether a key is available for `jev-live` / Windows live gates.
5. **Draft PR #1** — `docs/readme-intent-and-status-20260910` edits README/docs that Phase 1 will rewrite. Close, rebase, or supersede?
