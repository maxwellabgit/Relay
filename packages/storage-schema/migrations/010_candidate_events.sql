-- Ambient CandidateEvent persistence + suppression keys for quiet feedback.
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
