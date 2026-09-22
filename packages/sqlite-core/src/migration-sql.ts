/** Generated migration SQL shared by Node and Expo SQLite drivers. */
export const MIGRATION_SQL: Readonly<Record<number, string>> = {
  1: `-- Shared SQLite schema for Node, Tauri, and expo-sqlite adapters.

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
`,
  2: `CREATE TABLE IF NOT EXISTS memories (
  memory_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  key TEXT NOT NULL,
  value_json TEXT NOT NULL,
  source TEXT NOT NULL,
  created_at TEXT NOT NULL,
  UNIQUE(kind, key)
);

CREATE TABLE IF NOT EXISTS work_sessions (
  session_id TEXT PRIMARY KEY,
  started_at TEXT NOT NULL,
  ended_at TEXT,
  termination TEXT NOT NULL,
  episode_count INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS work_episodes (
  episode_id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES work_sessions(session_id),
  case_id TEXT,
  signature TEXT NOT NULL,
  outcome TEXT NOT NULL,
  started_at TEXT NOT NULL,
  completed_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_work_episodes_signature ON work_episodes(signature);

CREATE TABLE IF NOT EXISTS decision_receipts (
  receipt_id TEXT PRIMARY KEY,
  case_id TEXT,
  gate_id TEXT NOT NULL,
  policy_version TEXT NOT NULL,
  question_type TEXT NOT NULL,
  provider TEXT NOT NULL,
  probabilities_json TEXT NOT NULL,
  thresholds_json TEXT NOT NULL,
  selected_option TEXT,
  result TEXT NOT NULL,
  reason_code TEXT NOT NULL,
  latency_ms INTEGER,
  retries INTEGER NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS pattern_evidence (
  signature TEXT PRIMARY KEY,
  count INTEGER NOT NULL,
  session_ids_json TEXT NOT NULL,
  outcomes_json TEXT NOT NULL,
  first_at TEXT NOT NULL,
  last_at TEXT NOT NULL,
  evidence_ids_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS expansion_candidates (
  candidate_id TEXT PRIMARY KEY,
  signature TEXT NOT NULL,
  state TEXT NOT NULL,
  because TEXT NOT NULL,
  needed TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS review_runs (
  review_id TEXT PRIMARY KEY,
  trigger_code TEXT NOT NULL,
  at TEXT NOT NULL,
  findings_json TEXT NOT NULL,
  sessions_at_review INTEGER NOT NULL,
  episodes_at_review INTEGER NOT NULL,
  candidates_at_review INTEGER NOT NULL,
  built_reflexes_at_review INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS dead_letters (
  work_id TEXT PRIMARY KEY,
  reason_code TEXT NOT NULL,
  at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS review_cursors (
  trigger_code TEXT PRIMARY KEY,
  review_id TEXT NOT NULL,
  sessions_at_review INTEGER NOT NULL,
  episodes_at_review INTEGER NOT NULL,
  candidates_at_review INTEGER NOT NULL,
  built_reflexes_at_review INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_memories_kind_key ON memories(kind, key);
CREATE INDEX IF NOT EXISTS idx_receipts_case ON decision_receipts(case_id);
CREATE INDEX IF NOT EXISTS idx_episodes_session ON work_episodes(session_id);
CREATE INDEX IF NOT EXISTS idx_dead_letters_at ON dead_letters(at);
`,
  3: `CREATE TABLE IF NOT EXISTS judgment_attempts (
  attempt_id TEXT PRIMARY KEY,
  case_id TEXT NOT NULL,
  work_id TEXT,
  attempt INTEGER NOT NULL,
  max_attempts INTEGER NOT NULL,
  next_attempt_at TEXT,
  failure_category TEXT,
  provider_request_id TEXT,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_judgment_attempts_case ON judgment_attempts(case_id, attempt);
`,
  4: `-- Decision correlation fields for truthful console projection.
-- Do not invent joins across unrelated cases.

ALTER TABLE decision_receipts ADD COLUMN decision_id TEXT;
ALTER TABLE decision_receipts ADD COLUMN judgment_id TEXT;
ALTER TABLE decision_receipts ADD COLUMN reflex_id TEXT;
ALTER TABLE decision_receipts ADD COLUMN selected_option_id TEXT;
ALTER TABLE decision_receipts ADD COLUMN option_labels_json TEXT NOT NULL DEFAULT '{}';
ALTER TABLE decision_receipts ADD COLUMN requested_at TEXT;
ALTER TABLE decision_receipts ADD COLUMN completed_at TEXT;

CREATE INDEX IF NOT EXISTS idx_receipts_decision ON decision_receipts(decision_id);
CREATE INDEX IF NOT EXISTS idx_receipts_case_created ON decision_receipts(case_id, created_at);
`,
  5: `-- Feed content references protected artifacts; no prose in SQLite.

CREATE TABLE IF NOT EXISTS feed_items (
  item_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  content_artifact_id TEXT NOT NULL,
  content_sha256 TEXT NOT NULL,
  created_at TEXT NOT NULL,
  case_id TEXT
);

CREATE INDEX IF NOT EXISTS idx_feed_items_created
  ON feed_items (created_at);
`,
  6: `-- Protected learning content: free prose lives in artifacts, not SQLite columns.

ALTER TABLE memories ADD COLUMN content_artifact_id TEXT;
ALTER TABLE memories ADD COLUMN content_sha256 TEXT;
ALTER TABLE memories ADD COLUMN metadata_json TEXT NOT NULL DEFAULT '{}';

ALTER TABLE decision_receipts ADD COLUMN labels_artifact_id TEXT;
ALTER TABLE decision_receipts ADD COLUMN labels_sha256 TEXT;

ALTER TABLE expansion_candidates ADD COLUMN because_artifact_id TEXT;
ALTER TABLE expansion_candidates ADD COLUMN because_sha256 TEXT;

ALTER TABLE review_runs ADD COLUMN findings_artifact_id TEXT;
ALTER TABLE review_runs ADD COLUMN findings_sha256 TEXT;
`,
  7: `-- Application-level runtime settings (not session Listening).

CREATE TABLE IF NOT EXISTS app_settings (
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
`,
  8: `-- Marker: legacy plaintext learning columns scrubbed into protected artifacts.
-- Data migration runs in store open (Node ArtifactStorePort / Rust DPAPI put).
-- Columns already exist from 006_protected_learning.sql.

SELECT 1;
`,
  9: `-- Optional correlation chain on work items (schema-compatible additive columns).
ALTER TABLE work_items ADD COLUMN parent_work_id TEXT;
ALTER TABLE work_items ADD COLUMN correlation_id TEXT;
`,
  10: `-- Ambient CandidateEvent persistence + suppression keys for quiet feedback.
CREATE TABLE IF NOT EXISTS candidate_events (
  candidate_event_id TEXT PRIMARY KEY,
  source_event_id TEXT NOT NULL,
  case_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  subject_refs_json TEXT NOT NULL,
  source_slice_refs_json TEXT NOT NULL,
  extractor_version TEXT NOT NULL,
  status TEXT NOT NULL,
  urgency_reason TEXT,
  subject_key TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_candidate_events_case ON candidate_events(case_id);
CREATE INDEX IF NOT EXISTS idx_candidate_events_status ON candidate_events(status);
CREATE INDEX IF NOT EXISTS idx_candidate_events_subject ON candidate_events(subject_key);

CREATE TABLE IF NOT EXISTS ambient_suppressions (
  suppression_key TEXT PRIMARY KEY,
  reason TEXT NOT NULL,
  created_at TEXT NOT NULL
);
`,
  11: `-- Phase 7: candidate build metadata + per-event PatternEvidence rows
ALTER TABLE expansion_candidates ADD COLUMN meta_json TEXT;

CREATE TABLE IF NOT EXISTS pattern_evidence_events (
  evidence_id TEXT PRIMARY KEY,
  signature TEXT NOT NULL,
  source_class TEXT NOT NULL,
  route_or_tool TEXT,
  user_action TEXT NOT NULL,
  outcome_class TEXT NOT NULL,
  duplicate_count INTEGER NOT NULL DEFAULT 0,
  time_to_action_ms INTEGER,
  feedback TEXT,
  case_id TEXT,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_pattern_evidence_events_signature
  ON pattern_evidence_events(signature);
`,
  12: `-- Authoritative hosted-judgment grants. Balances are counters, not a bounded event scan.

CREATE TABLE IF NOT EXISTS hosted_grants (
  grant_id TEXT PRIMARY KEY,
  scope_kind TEXT NOT NULL,
  scope_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  allowed_json TEXT NOT NULL,
  max_requests INTEGER NOT NULL,
  max_bytes INTEGER NOT NULL,
  revoked_at TEXT,
  requests_committed INTEGER NOT NULL DEFAULT 0,
  bytes_committed INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS hosted_grant_reservations (
  reservation_id TEXT PRIMARY KEY,
  grant_id TEXT NOT NULL,
  bytes INTEGER NOT NULL,
  state TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_hosted_grants_scope ON hosted_grants(scope_kind, scope_id);
`,
};
