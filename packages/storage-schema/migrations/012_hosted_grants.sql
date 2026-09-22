-- Authoritative hosted-judgment grants. Balances are counters, not a bounded event scan.

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
