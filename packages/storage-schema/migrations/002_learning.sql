CREATE TABLE IF NOT EXISTS memories (
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
