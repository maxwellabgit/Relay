# 04 · Data model

Everything Relay is authoritative for is a file under the data root. There is no database in slice 1; a database arrives only when indexing and projections require it, and it will always be a rebuildable projection of the ledger plus project files.

Identifiers are ULIDs (26 chars, Crockford base32, time-ordered, `Relay.Core.Ids.Ulid`). Timestamps are UTC ISO-8601 with 7 fractional digits.

## 1. Ledger (`ledger\relay-ledger.jsonl`) — authoritative for *what happened*

One JSON object per line, UTF-8, `\n` terminated. Fields, in this fixed order:

```json
{"seq":14,"id":"01M1NAXN82A2QGASKBPPTXXGXW","ts":"2026-09-04T04:30:37.8262533Z","session":"01M1NAX8DF5KJZXR88J7R71DDJ","type":"capture.committed","prev":"<sha256 of previous line>","data":{...},"hash":"<sha256>"}
```

- `hash` = SHA-256 (lower hex) of the UTF-8 bytes of the line with the trailing `,"hash":"…"` member removed.
- `prev` = `hash` of the previous record; the first record uses 64 zeros.
- `seq` starts at 1 and increases by exactly 1.
- Records are appended with `FileOptions.WriteThrough` and `Flush(flushToDisk: true)`; the writer holds the file open with `FileShare.Read`.

Verification (`LedgerVerifier.Verify`) walks the file once and yields one of:

| Health | Meaning | Startup action |
| --- | --- | --- |
| `Ok` | every line decodes, every `hash`/`prev`/`seq` matches | continue |
| `TornTail` | all lines good except the last, which is incomplete or unterminated | move tail bytes to `ledger\quarantine\{utc}-torn-tail.bin`, truncate, record `ledger.repaired` |
| `IntegrityFailure` | a complete line fails its hash, chain, or sequence check | `LOCKED`; record `ledger.integrity_failed{brokenSeq, reason}`; new records continue after the last verified line so the break is permanent evidence |

### 1.1 Event catalog (slice 1)

`data` payloads, by `type`. Field names are stable; add fields, never rename.

| Type | Payload | Notes |
| --- | --- | --- |
| `session.started` | `sessionId, pid, appVersion, dataRoot, settingsHash` | first record of every run |
| `session.ended` | `reason, finalState, activeCaptureId` | last record of a clean run |
| `session.crash_detected` | `crashedSessionId, crashedPid, crashedStartedAt` | for each session record with no `endedAt` |
| `ledger.verified` | `records, lastHash, health, reason` | |
| `ledger.repaired` | `quarantinedBytes, quarantinePath, recordsKept` | |
| `ledger.integrity_failed` | `brokenSeq, reason, recordsVerified` | followed by `lock.engaged` |
| `settings.loaded` | `path, hash, createdDefault` | |
| `settings.invalid` | `problem` | one per problem; default in effect |
| `storage.acl_applied` / `storage.acl_failed` | `path[, error]` | |
| `hotkey.registered` / `hotkey.registration_failed` | `name, chord[, error]` | `name` ∈ `NOTE_KEY`, `COMMAND_KEY` |
| `hotkey.rejected` | `key, state, reason` | press that the state machine refused |
| `state.changed` | `from, to, trigger` | one per accepted, state-changing transition |
| `capture.started` | `captureId, mode, previousForegroundProcess, flowRelayEnabled` | `mode` ∈ `note`, `command` |
| `capture.focus_lost` / `capture.focus_regained` | `captureId` | |
| `capture.stop_requested` | `captureId, chars` | |
| `capture.transcript_stable` | `captureId, chars` | |
| `capture.transcript_timeout` | `captureId, waitedMs, extensions` | |
| `capture.wait_extended` | `captureId, extensions` | Retry wait |
| `capture.submitted_early` | `captureId, chars` | Submit now |
| `capture.committed` | `captureId, mode, chars, sha256, startedAt, stoppedAt, source, text` | **the only record that contains capture text** |
| `capture.cancelled` | `captureId, mode, chars, stateAtCancel` | never contains text |
| `capture.draft_recovered` | `captureId, originalCaptureId, mode, chars` | Recover draft creates a new capture id |
| `capture.interrupted_found` | `captureId, mode, chars, startedAt, updatedAt, alreadyResolved[, resolution]` | |
| `capture.interrupted_discarded` | `captureId, mode, chars, path` | |
| `capture.organize_failed` | `captureId, error, committed` | `committed` tells Retry whether to skip the commit |
| `note.draft_created` | `noteId, captureId, sourceEventId, chars, path` | |
| `command.recorded` | `captureId, sourceEventId, chars, executed:false` | |
| `flow.relay_sent` | `captureId, purpose, chord, ok, error` | `purpose` ∈ `start`, `stop` |
| `flow.relay_skipped` | `captureId, purpose, reason` | |
| `app.failed` | `where, exceptionType, message, incidentPath` | |
| `lock.engaged` | `reason[, brokenSeq]` | |
| `lock.released` | `acknowledged, by` | |

Future phases add `proposal.*`, `approval.*`, `execution.*`, `project.*`, `note.*`, `agent_run.*`, `artifact.*` families in the same envelope.

## 2. Session records (`sessions\{sessionId}.json`)

```json
{ "sessionId": "…", "pid": 7852, "appVersion": "0.1.0", "startedAt": "…", "endedAt": null, "cleanShutdown": false, "endedBy": null }
```

Written at start; `endedAt`, `cleanShutdown: true` and `endedBy` are set at clean shutdown. A record with `endedAt == null` whose `pid` is not alive means a crash; the next start records `session.crash_detected` and sets `endedAt` to the detection time with `endedBy` naming the detecting session.

## 3. Crash-safe draft (`staging\drafts\current.json`)

```json
{ "captureId": "…", "mode": "note", "sessionId": "…", "startedAt": "…", "updatedAt": "…", "text": "…", "previousForegroundProcess": "Relay" }
```

- Exactly zero or one exists. Written before the surface is focused, rewritten atomically on a 200 ms debounce as text arrives, removed only after `capture.committed` **and** the derived record succeed, or on cancel.
- On start, if present: if the ledger already has `capture.committed`, `capture.cancelled`, or `capture.interrupted_discarded` for that `captureId`, the file is redundant and is removed (`alreadyResolved: true`). Otherwise it is an interrupted capture and appears in Review.
- An undecodable draft is moved to `staging\drafts\discarded\unreadable-{utc}.json`, never deleted.

## 4. Draft notes (`staging\notes\{noteId}.json`) — verbatim, unrouted, no model

```json
{
  "noteId": "01M1NAXN8452VC6VFXQZ9WR41K",
  "captureId": "01M1NAXD1ENGP6D8V0WPQZP56G",
  "sourceEventId": "01M1NAXN82A2QGASKBPPTXXGXW",
  "createdAt": "2026-09-04T04:30:37.83Z",
  "type": "raw-capture",
  "status": "draft",
  "projectId": null,
  "routing": "unrouted",
  "confidence": null,
  "text": "…verbatim…",
  "spans": [ { "eventId": "01M1NAXN82A2QGASKBPPTXXGXW", "start": 0, "end": 110 } ]
}
```

`spans` is the traceability primitive required by the memory rules: every derived note, now and later, carries half-open character ranges into the `text` of specific `capture.committed` events. Extraction (phase 6) will produce many notes per capture, each with narrower spans, and never modify the raw note.

## 5. Settings (`config\settings.json`)

```json
{
  "schemaVersion": 1,
  "hotkeys":    { "noteKey": "F13", "commandKey": "F14" },
  "flowRelay":  { "enabled": false, "handsFreeChord": "Ctrl+Win+F24", "startDelayMs": 200 },
  "capture":    { "transcriptTimeoutMs": 10000, "stabilizationMs": 1500, "stabilizationWithoutRelayMs": 600,
                  "completedReceiptMs": 4000, "draftPersistDebounceMs": 200 },
  "diagnostics": { "flowProcessNames": ["Wispr Flow", "WisprFlow", "Flow"] }
}
```

Chord grammar: `[Ctrl+][Alt+][Shift+][Win+]<Key>` where `<Key>` is `F1`–`F24`, `A`–`Z`, `0`–`9`, or a named key (`Space`, `Tab`, `Enter`, `Esc`, `Pause`, `Insert`, `Home`, `End`, …). Validation: both hotkeys must parse and differ; the relay chord must differ from both; timeouts have floors. Invalid sections fall back to defaults and are reported in Review. The SHA-256 of the effective settings is recorded in `session.started` and `settings.loaded`.

## 6. Manifest (`relay.json`)

`{ "schemaVersion": 1, "instanceId": "<ulid>", "createdAt": "…" }` — identifies the data root; used later for migrations and to name the single-instance mutex.

## 7. Incidents (`incidents\{utc}-{kind}.json`)

Written when the process fails or the ledger cannot be written: `kind`, `summary`, `detail` (exception text), `at`, `sessionId`, `state`, `pid`. They contain no capture text. Kinds in slice 1: `app_failed`, `ledger_write_failed`, `ledger_integrity_failed`, `startup_failed`.

## 8. Later tables (contract §9), all as ledger-linked files

| Collection | Location | Authoritative for |
| --- | --- | --- |
| `projects` | `registry\projects.jsonl` (+ `project.toml` in each folder) | identity, aliases, roots, policy |
| `notes` / `decisions` / `tasks` | inside project folders, versioned, each with `spans` | user content |
| `sources` | the `spans` arrays plus `note.*` ledger events | traceability |
| `proposals`, `approvals`, `executions` | ledger only | action lifecycle |
| `agent_runs` | ledger + `staging\agents\{runId}\` | worker assignments and outputs |
| `artifacts` | `.orchestrator\artifacts.jsonl` per project | file hashes, ownership, versions |
| search index | `index\` | disposable projection |
