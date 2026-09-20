CREATE TABLE IF NOT EXISTS judgment_attempts (
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
