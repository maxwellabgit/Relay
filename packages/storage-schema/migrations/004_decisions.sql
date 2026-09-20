-- Decision correlation fields for truthful console projection.
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
