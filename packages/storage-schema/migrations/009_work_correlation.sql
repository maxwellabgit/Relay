-- Optional correlation chain on work items (schema-compatible additive columns).
ALTER TABLE work_items ADD COLUMN parent_work_id TEXT;
ALTER TABLE work_items ADD COLUMN correlation_id TEXT;
