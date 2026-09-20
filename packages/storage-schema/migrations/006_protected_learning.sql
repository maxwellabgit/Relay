-- Protected learning content: free prose lives in artifacts, not SQLite columns.

ALTER TABLE memories ADD COLUMN content_artifact_id TEXT;
ALTER TABLE memories ADD COLUMN content_sha256 TEXT;
ALTER TABLE memories ADD COLUMN metadata_json TEXT NOT NULL DEFAULT '{}';

ALTER TABLE decision_receipts ADD COLUMN labels_artifact_id TEXT;
ALTER TABLE decision_receipts ADD COLUMN labels_sha256 TEXT;

ALTER TABLE expansion_candidates ADD COLUMN because_artifact_id TEXT;
ALTER TABLE expansion_candidates ADD COLUMN because_sha256 TEXT;

ALTER TABLE review_runs ADD COLUMN findings_artifact_id TEXT;
ALTER TABLE review_runs ADD COLUMN findings_sha256 TEXT;
