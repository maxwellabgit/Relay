-- Pass 1: executions stay distinct from durable project Cases.
-- Legacy cases rows are copied; case_id is the execution id, never a project Case id.

CREATE TABLE IF NOT EXISTS executions (
  execution_id TEXT PRIMARY KEY,
  version INTEGER NOT NULL,
  origin TEXT NOT NULL,
  kind TEXT NOT NULL,
  status TEXT NOT NULL,
  phase TEXT NOT NULL,
  priority INTEGER NOT NULL,
  parent_execution_id TEXT,
  wait_kind TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

INSERT INTO executions (
  execution_id, version, origin, kind, status, phase, priority,
  parent_execution_id, wait_kind, created_at, updated_at
)
SELECT
  case_id, version, origin, kind, status, phase, priority,
  parent_case_id, wait_kind, created_at, updated_at
FROM cases
WHERE NOT EXISTS (
  SELECT 1 FROM executions AS existing WHERE existing.execution_id = cases.case_id
);

CREATE TABLE IF NOT EXISTS execution_case_links (
  execution_id TEXT NOT NULL,
  project_case_id TEXT NOT NULL,
  linked_at TEXT NOT NULL,
  PRIMARY KEY (execution_id, project_case_id)
);

CREATE TABLE IF NOT EXISTS foundation_records (
  kind TEXT NOT NULL,
  record_id TEXT NOT NULL,
  version INTEGER NOT NULL,
  payload_json TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (kind, record_id)
);

CREATE INDEX IF NOT EXISTS idx_foundation_kind ON foundation_records(kind, updated_at);
