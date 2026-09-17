# 05 · Storage layout: data root and project folders

Two distinct trees, with different owners and different rules.

| Tree | Location | Owner | Contents |
| --- | --- | --- | --- |
| **Data root** | `%LOCALAPPDATA%\Relay` (override: `RELAY_DATA_ROOT`) | Relay only | ledger, staging, sessions, config, incidents — *never* user content in the project sense |
| **Project roots** (*later*) | user-chosen folders, e.g. `%USERPROFILE%\Documents\Projects\<slug>` | user; Relay writes via the executor after approval | canonical notes, decisions, tasks, conversations, artifacts |

## 1. Data root (implemented)

```
%LOCALAPPDATA%\Relay\                 ACL: current user + SYSTEM, inheritance disabled
  relay.json                          manifest: schemaVersion, instanceId, createdAt
  config\
    settings.json                     user settings (see 04-data-model §5)
  ledger\
    relay-ledger.jsonl                append-only hash-chained event ledger (authoritative)
    quarantine\                       torn-tail bytes preserved by repair; never deleted
  sessions\
    {sessionId}.json                  liveness record per run; crash detection
  staging\
    drafts\
      current.json                    the in-progress capture (0 or 1 file)
      discarded\
        {captureId}.json              interrupted drafts the user declined to keep
        unreadable-{utc}.json         drafts that could not be parsed
    notes\
      {noteId}.json                   verbatim draft notes, unrouted, with source spans
  incidents\
    {utc}-{kind}.json                 failures, including ones the ledger could not record
  logs\                               reserved for redacted diagnostics; nothing writes here yet
```

Rules:

- **Nothing is deleted** except `staging\drafts\current.json` once its content is durably represented elsewhere (committed to the ledger + derived record written, or cancelled by the user). Everything else is moved or appended.
- Every file is written via `AtomicFile` (temp file → flush to disk → replace), so a crash never leaves a half-written JSON file.
- `staging\` is the only place a future model or worker output may land. Promotion out of staging is always a typed, ledger-recorded, and (where required) approved executor operation.
- The ACL is re-applied on every start; if it fails (`storage.acl_failed`) Relay still runs and shows the problem.

## 2. Project folder contract (phase 2/4; no code yet)

A project is identified by an immutable ULID. The folder name is a mutable *slug*; renames are ledger events, never identity changes.

```
<project-root>\
  project.toml            id, slug, name, aliases, status, created, policy (allowed tools, network, workers)
  overview.md             human-maintained summary; Relay may propose edits, never apply without approval
  notes\                  atomic notes: one file per note, YAML front matter + body
  decisions\              decision records; a new decision may supersede but never erase an old one
  tasks\                  task records with status history
  conversations\          command-mode turns that touched this project (verbatim transcripts + orchestrator responses)
  artifacts\              registered files (documents, code, exports) with hashes in .orchestrator
  .orchestrator\          Relay-owned metadata; models and workers cannot write here
    artifacts.jsonl       file manifest: path, sha256, owner, status, version, source event ids
    versions\             prior versions of any canonical file Relay modified (append-only)
    sources.jsonl         note → event span links, mirrored from the ledger for local queries
    staging\              project-scoped proposals, diffs, and worker outputs awaiting approval
```

Front matter of every note (memory rule 3):

```yaml
---
id: 01M1…            # note ULID
project: 01M1…       # project ULID
type: idea | fact | question | decision | reference | task
status: draft | active | disputed | superseded | archived
created: 2026-09-04T04:30:37Z
confidence: 0.82     # routing confidence at creation; null when placed by the user
spans:
  - { eventId: 01M1…, start: 0, end: 110 }   # exact source characters in the ledger
supersedes: [ ]      # ids this note replaces; the replaced notes stay on disk marked superseded
---
```

## 3. Lifecycle rules

| Operation | Tier | Effect on disk | Ledger |
| --- | --- | --- | --- |
| Create project | approval | create folder tree + `project.toml`; register in `registry\projects.jsonl` | `project.created` with path, id, manifest hash |
| Route a draft note into a project | automatic if confidence ≥ threshold, otherwise Review | copy from `staging\notes` into `notes\`; staging copy marked `promoted` (kept) | `note.routed{noteId, projectId, confidence, by}` |
| Modify a canonical note | approval | previous version copied to `.orchestrator\versions\`, new content written atomically | `note.modified{noteId, fromHash, toHash, proposalId}` |
| Rename / move a project | approval | folder moved; `project.toml` unchanged id; registry updated | `project.moved{oldPath, newPath}` |
| Archive a project (the only "delete") | approval | folder moved to `<archive-root>\{slug}-{id}\` with a manifest of hashes; recovery deadline recorded | `project.archived{oldPath, newPath, manifestHash, recoverUntil}` |
| Permanent deletion | prohibited | — | — |

## 4. Path safety (executor requirements)

- Every target path is canonicalized with `Path.GetFullPath` and reparse points resolved with `FileSystemInfo.ResolveLinkTarget(true)` before the prefix check against the registered root.
- Case-insensitive prefix comparison on Windows, and the comparison is done on the *resolved* path, so `..`, junctions, symlinks, 8.3 names, and case variants cannot escape.
- Writes go through the executor only; it opens files with `FileShare.None`, writes to a sibling temp file, and replaces.
- Registered roots are stored in `config\workspaces.json` and can only be added through the UI, never by a proposal.
