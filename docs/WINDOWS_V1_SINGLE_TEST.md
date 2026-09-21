# Windows V1 single test

One typed Ask. No microphone, no local model, no TypeSafe key.

This is the check that `resolve-acronym@1` runs on the desktop workbench. It is not the full dogfood list and not `readiness:dogfood`.

## Start

From the repo root, on `main`:

```powershell
npm run dev:desktop
```

Leave **Listen off**.

## Ask

In the Ask field, type exactly:

```text
What does API mean?
```

The screen should show **Application Programming Interface**.

## Where the log is

The desktop app writes here:

```text
%LOCALAPPDATA%\RELAY\runs\<runId>\
  manifest.json
  events.jsonl
```

On this machine that is `C:\Users\maxwe\AppData\Local\RELAY\runs\`.

Use the newest `run_*` folder. While the window is open, `manifest.json` stays `"status": "running"`. That is expected.

Do not look in the repository `runs\` folder. `dev/run-relay.ps1` creates a directory there, but the Tauri trace does not.

## What a pass looks like

`events.jsonl` for that Ask, in order:

```text
source.accepted     reasonCode direct_answer
case.created        same caseId
reflex.detected     reflexId reflex.resolve-acronym
policy.evaluated    reasonCode exact_glossary
answer.committed
episode.recorded
outcome.recorded
```

`direct_answer` means a typed Ask. `exact_glossary` means the bundled dictionary. There must be no model event and no Jev event.

The question and the expansion must not appear in `events.jsonl` or `manifest.json`. A log with no visible answer text is still a pass when the events above are present.

## Stop

Close the RELAY window. Do not treat this file as proof of Listen, the local model, hosted Jev, or the installed NSIS build.
