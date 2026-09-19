-- Shared SQLite schema for Node, Tauri, and expo-sqlite adapters.

CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
  session_id TEXT PRIMARY KEY,
  created_at TEXT NOT NULL,
  listening INTEGER NOT NULL DEFAULT 0,
  metadata_json TEXT NOT NULL DEFAULT '{}'
);

CREATE TABLE IF NOT EXISTS source_events (
  source_event_id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL,
  segment_id TEXT NOT NULL,
  revision INTEGER NOT NULL,
  sequence INTEGER NOT NULL,
  origin TEXT NOT NULL,
  speaker_key TEXT,
  start_ms INTEGER NOT NULL,
  end_ms INTEGER NOT NULL,
  final INTEGER NOT NULL,
  text_artifact_id TEXT NOT NULL,
  text_sha256 TEXT NOT NULL,
  policy_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  UNIQUE(session_id, segment_id, revision)
);

CREATE TABLE IF NOT EXISTS cases (
  case_id TEXT PRIMARY KEY,
  version INTEGER NOT NULL,
  origin TEXT NOT NULL,
  kind TEXT NOT NULL,
  status TEXT NOT NULL,
  phase TEXT NOT NULL,
  priority INTEGER NOT NULL,
  parent_case_id TEXT,
  wait_kind TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS case_events (
  event_id TEXT PRIMARY KEY,
  case_id TEXT NOT NULL,
  case_version INTEGER NOT NULL,
  type TEXT NOT NULL,
  at TEXT NOT NULL,
  payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS judgments (
  judgment_id TEXT PRIMARY KEY,
  provider TEXT,
  question_set_id TEXT NOT NULL,
  question_set_version TEXT NOT NULL,
  model TEXT NOT NULL,
  status TEXT NOT NULL,
  case_id TEXT,
  case_version INTEGER,
  request_artifact_id TEXT,
  request_hash TEXT,
  response_artifact_id TEXT,
  response_hash TEXT,
  failure_category TEXT,
  input_tokens INTEGER,
  output_tokens INTEGER,
  elapsed_ms INTEGER,
  created_at TEXT NOT NULL,
  completed_at TEXT
);

CREATE TABLE IF NOT EXISTS reflex_definitions (
  reflex_id TEXT NOT NULL,
  version INTEGER NOT NULL,
  definition_json TEXT NOT NULL,
  definition_hash TEXT NOT NULL,
  PRIMARY KEY (reflex_id, version)
);

CREATE TABLE IF NOT EXISTS reflex_runs (
  run_id TEXT PRIMARY KEY,
  reflex_id TEXT NOT NULL,
  reflex_version INTEGER NOT NULL,
  case_id TEXT NOT NULL,
  status TEXT NOT NULL,
  result_type TEXT,
  created_at TEXT NOT NULL,
  completed_at TEXT
);

CREATE TABLE IF NOT EXISTS findings (
  finding_id TEXT PRIMARY KEY,
  case_id TEXT NOT NULL,
  reflex_id TEXT,
  reflex_version INTEGER,
  summary TEXT NOT NULL,
  evidence_json TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS operations (
  operation_id TEXT PRIMARY KEY,
  case_id TEXT NOT NULL,
  case_version INTEGER NOT NULL,
  connection_id TEXT NOT NULL,
  action_json TEXT NOT NULL,
  canonical_hash TEXT NOT NULL,
  status TEXT NOT NULL,
  arguments_artifact_id TEXT,
  created_at TEXT NOT NULL,
  completed_at TEXT
);

CREATE TABLE IF NOT EXISTS approvals (
  operation_id TEXT PRIMARY KEY,
  case_version INTEGER NOT NULL,
  connection_id TEXT NOT NULL,
  connection_version INTEGER NOT NULL,
  canonical_hash TEXT NOT NULL,
  summary TEXT NOT NULL,
  action_json TEXT NOT NULL,
  granted_scopes_json TEXT NOT NULL,
  write_action_enabled INTEGER NOT NULL,
  bound_action_version INTEGER NOT NULL,
  proposed_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS work_items (
  work_id TEXT PRIMARY KEY,
  type TEXT NOT NULL,
  priority INTEGER NOT NULL,
  available_at TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  lease_owner TEXT,
  lease_until TEXT,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS domain_events (
  sequence INTEGER PRIMARY KEY AUTOINCREMENT,
  type TEXT NOT NULL,
  at TEXT NOT NULL,
  payload_json TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_work_items_ready
  ON work_items (available_at, priority DESC, created_at);

CREATE INDEX IF NOT EXISTS idx_source_events_session
  ON source_events (session_id, sequence);

CREATE INDEX IF NOT EXISTS idx_cases_status
  ON cases (status, priority DESC);

CREATE INDEX IF NOT EXISTS idx_judgments_request_hash
  ON judgments (request_hash, status);
