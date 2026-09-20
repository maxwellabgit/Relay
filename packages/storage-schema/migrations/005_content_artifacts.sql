-- Feed content references protected artifacts; no prose in SQLite.

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
