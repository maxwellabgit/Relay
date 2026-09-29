# ADR-003: Local workflow observation

Status: accepted for the Windows workflow release.

## Decision

Computer activity is a new local stream. It does not replace ProjectCase, Verify, Reflex definitions, or the tool broker.

- Adapters emit raw signals. `acceptSignal` and the native host reject malformed, oversized, secret, and tool-shaped payloads before they become observations.
- Chrome talks only to `relay-browser-bridge.exe` over Native Messaging (`stdio`). The host appends validated observations to `%LOCALAPPDATA%\RELAY\bridge\inbox.jsonl`. It cannot run RELAY tools. The desktop process drains that inbox.
- The Windows observer uses `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` on a background thread and samples `GetForegroundWindow` every 400ms when the hook is unavailable. It records process name, window title, and focus duration. It does not read keystrokes, the screen, or the clipboard.
- Episodes are grouped by a 25-minute gap and shared job, resume, or application evidence. A listing with no resume or application activity is `job_research`, not a submitted application.
- A repeated job-application pattern becomes a proposal. Accepting it creates `reflex.job-application` version 1 in shadow mode. Files are written only after the user activates that version and asks for drafts. The master resume is read, never overwritten.
- Raw observations are overwritten after the retention window (default 7 days) or when the user deletes observation history. Episode summaries, Cases, and approved Reflex versions remain.
- Jev is not called for ordinary focus changes. The user can ask for a bounded Choice on an episode that is still `uncertain`. Page text is not included in that request.

## Departures

- The first resume path reads `.txt`, `.md`, and `.docx` (plain text extracted from `word/document.xml`) and writes Markdown copies under `%LOCALAPPDATA%\RELAY\drafts\`. It does not edit a Word master in place.
- Filesystem watchers are not enabled. Resume activity is inferred from the foreground window title.
- Session site grants live in the extension's `chrome.storage.session` and in the desktop process. They are not a second permission system.
