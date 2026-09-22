-- Phase 7: candidate build metadata + per-event PatternEvidence rows
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
