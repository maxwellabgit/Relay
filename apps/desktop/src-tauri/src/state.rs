use rusqlite::{params, Connection, OptionalExtension, TransactionBehavior};
use serde_json::{json, Value};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

const MIGRATION_1: &str =
    include_str!("../../../../packages/storage-schema/migrations/001_core.sql");
const MIGRATION_2: &str =
    include_str!("../../../../packages/storage-schema/migrations/002_learning.sql");
const MIGRATION_3: &str =
    include_str!("../../../../packages/storage-schema/migrations/003_runtime.sql");
const MIGRATION_4: &str =
    include_str!("../../../../packages/storage-schema/migrations/004_decisions.sql");
const MIGRATION_5: &str =
    include_str!("../../../../packages/storage-schema/migrations/005_content_artifacts.sql");
const MIGRATION_6: &str =
    include_str!("../../../../packages/storage-schema/migrations/006_protected_learning.sql");
const MIGRATION_7: &str =
    include_str!("../../../../packages/storage-schema/migrations/007_runtime_settings.sql");
const MIGRATION_8: &str =
    include_str!("../../../../packages/storage-schema/migrations/008_protect_legacy_content.sql");
const MIGRATION_9: &str =
    include_str!("../../../../packages/storage-schema/migrations/009_work_correlation.sql");
const MIGRATION_10: &str =
    include_str!("../../../../packages/storage-schema/migrations/010_candidate_events.sql");
const MIGRATION_11: &str =
    include_str!("../../../../packages/storage-schema/migrations/011_pattern_evidence_events.sql");
const MIGRATION_12: &str =
    include_str!("../../../../packages/storage-schema/migrations/012_hosted_grants.sql");
const MIGRATION_13: &str =
    include_str!("../../../../packages/storage-schema/migrations/013_case_foundation.sql");
const RETENTION_MS: i64 = 7 * 24 * 60 * 60 * 1000;

pub struct StateDb {
    conn: Connection,
    /// Depth of an explicit multi-op transaction opened via begin_transaction.
    txn_depth: u32,
    root: PathBuf,
}

impl StateDb {
    pub fn open_default() -> Result<Self, String> {
        let path = std::env::var("RELAY_STATE_PATH")
            .ok()
            .filter(|value| !value.is_empty())
            .map(PathBuf::from)
            .unwrap_or_else(|| {
                let base = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into());
                PathBuf::from(base).join("RELAY").join("state.sqlite")
            });
        Self::open(&path)
    }

    pub fn open(path: &Path) -> Result<Self, String> {
        if let Some(parent) = path.parent() {
            if !parent.as_os_str().is_empty() {
                std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
            }
        }
        let conn = Connection::open(path).map_err(|error| error.to_string())?;
        conn.pragma_update(None, "foreign_keys", "ON")
            .map_err(|error| error.to_string())?;
        let root = path
            .parent()
            .map(Path::to_path_buf)
            .filter(|parent| !parent.as_os_str().is_empty())
            .unwrap_or_else(|| PathBuf::from("."));
        let mut db = Self {
            conn,
            txn_depth: 0,
            root,
        };
        db.migrate()?;
        Ok(db)
    }

    pub fn execute(&mut self, op: &Value) -> Result<Value, String> {
        match op.get("op").and_then(|value| value.as_str()).unwrap_or("") {
            "begin_transaction" => self.begin_transaction(),
            "commit_transaction" => self.commit_transaction(),
            "rollback_transaction" => self.rollback_transaction(),
            "case_folder_write" => self.case_folder_write(op),
            "case_folder_read" => self.case_folder_read(op),
            _ if self.txn_depth > 0 => dispatch(&self.conn, op),
            _ => {
                let tx = self
                    .conn
                    .transaction_with_behavior(TransactionBehavior::Immediate)
                    .map_err(|error| error.to_string())?;
                let result = dispatch(&tx, op);
                match result {
                    Ok(value) => {
                        tx.commit().map_err(|error| error.to_string())?;
                        Ok(value)
                    }
                    Err(error) => {
                        let _ = tx.rollback();
                        Err(error)
                    }
                }
            }
        }
    }

    fn begin_transaction(&mut self) -> Result<Value, String> {
        if self.txn_depth > 0 {
            return Err("transaction_busy".into());
        }
        self.conn
            .execute_batch("BEGIN IMMEDIATE")
            .map_err(|error| error.to_string())?;
        self.txn_depth = 1;
        Ok(Value::Null)
    }

    fn commit_transaction(&mut self) -> Result<Value, String> {
        if self.txn_depth == 0 {
            return Err("no_transaction".into());
        }
        self.conn
            .execute_batch("COMMIT")
            .map_err(|error| error.to_string())?;
        self.txn_depth = 0;
        Ok(Value::Null)
    }

    fn rollback_transaction(&mut self) -> Result<Value, String> {
        if self.txn_depth == 0 {
            return Ok(Value::Null);
        }
        self.txn_depth = 0;
        self.conn
            .execute_batch("ROLLBACK")
            .map_err(|error| error.to_string())?;
        Ok(Value::Null)
    }

    fn migrate(&mut self) -> Result<(), String> {
        let tx = self.conn.transaction().map_err(|error| error.to_string())?;
        tx.execute_batch(MIGRATION_1)
            .map_err(|error| error.to_string())?;
        tx.execute(
            "INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (1, ?1)",
            params![now_iso()],
        )
        .map_err(|error| error.to_string())?;
        apply_version(&tx, 2, MIGRATION_2)?;
        apply_version(&tx, 3, MIGRATION_3)?;
        apply_version(&tx, 4, MIGRATION_4)?;
        apply_version(&tx, 5, MIGRATION_5)?;
        apply_version(&tx, 6, MIGRATION_6)?;
        apply_version(&tx, 7, MIGRATION_7)?;
        apply_version(&tx, 8, MIGRATION_8)?;
        apply_version(&tx, 9, MIGRATION_9)?;
        apply_version(&tx, 10, MIGRATION_10)?;
        apply_version(&tx, 11, MIGRATION_11)?;
        apply_version(&tx, 12, MIGRATION_12)?;
        apply_version(&tx, 13, MIGRATION_13)?;
        tx.commit().map_err(|error| error.to_string())?;
        crate::grants::release_uncommitted_on_open(&self.conn)?;
        self.migrate_legacy_protected_content()?;
        Ok(())
    }

    fn migrate_legacy_protected_content(&mut self) -> Result<(), String> {
        migrate_legacy_memories(&self.conn)?;
        migrate_legacy_receipts(&self.conn)?;
        migrate_legacy_candidates(&self.conn)?;
        migrate_legacy_reviews(&self.conn)?;
        Ok(())
    }
}

fn apply_version(conn: &Connection, version: i64, sql: &str) -> Result<(), String> {
    let applied: Option<i64> = conn
        .query_row(
            "SELECT version FROM schema_migrations WHERE version = ?1",
            params![version],
            |row| row.get(0),
        )
        .optional()
        .map_err(|error| error.to_string())?;
    if applied.is_some() {
        return Ok(());
    }
    conn.execute_batch(sql).map_err(|error| error.to_string())?;
    conn.execute(
        "INSERT INTO schema_migrations(version, applied_at) VALUES (?1, ?2)",
        params![version, now_iso()],
    )
    .map_err(|error| error.to_string())?;
    Ok(())
}

fn migrate_legacy_memories(conn: &Connection) -> Result<(), String> {
    let mut stmt = conn
        .prepare(
            "SELECT memory_id, kind, value_json, metadata_json FROM memories
             WHERE value_json IS NOT NULL AND value_json != '{}'
               AND (content_artifact_id IS NULL OR content_artifact_id = '')",
        )
        .map_err(|e| e.to_string())?;
    let rows: Vec<(String, String, String, String)> = stmt
        .query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, String>(2)?,
                row.get::<_, Option<String>>(3)?
                    .unwrap_or_else(|| "{}".into()),
            ))
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    drop(stmt);
    for (memory_id, kind, value_json, metadata_json) in rows {
        let value: Value = parse_json(&value_json).unwrap_or(json!({}));
        if !value.is_object() {
            conn.execute(
                "UPDATE memories SET value_json='{}' WHERE memory_id=?1",
                params![memory_id],
            )
            .map_err(|e| e.to_string())?;
            continue;
        }
        let obj = value.as_object().cloned().unwrap_or_default();
        let mut existing_meta: serde_json::Map<String, Value> = parse_json(&metadata_json)
            .ok()
            .and_then(|v| v.as_object().cloned())
            .unwrap_or_default();
        let mut prose = serde_json::Map::new();
        if kind == "glossary" {
            if let Some(expansion) = obj.get("expansion").and_then(|v| v.as_str()) {
                if !expansion.is_empty() {
                    prose.insert("expansion".into(), json!(expansion));
                }
            }
            for (k, v) in &obj {
                if k == "expansion" {
                    continue;
                }
                if let Some(s) = v.as_str() {
                    existing_meta.insert(k.clone(), json!(s));
                }
            }
        } else {
            if let Some(display) = obj.get("displayName").and_then(|v| v.as_str()) {
                if !display.is_empty() {
                    prose.insert("displayName".into(), json!(display));
                }
            }
            for (k, v) in &obj {
                if k == "displayName" {
                    continue;
                }
                if let Some(s) = v.as_str() {
                    existing_meta.insert(k.clone(), json!(s));
                }
            }
        }
        let plain = serde_json::to_vec(&Value::Object(prose)).map_err(|e| e.to_string())?;
        let put = crate::artifacts::put_plain_bytes(&plain)?;
        conn.execute(
            "UPDATE memories
             SET content_artifact_id=?1, content_sha256=?2, metadata_json=?3, value_json='{}'
             WHERE memory_id=?4",
            params![
                put.artifact_id,
                put.sha256,
                Value::Object(existing_meta).to_string(),
                memory_id
            ],
        )
        .map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn migrate_legacy_receipts(conn: &Connection) -> Result<(), String> {
    struct LegacyReceipt {
        receipt_id: String,
        selected_option: Option<String>,
        selected_option_id: Option<String>,
        labels_json: String,
        labels_id: Option<String>,
        labels_sha: Option<String>,
    }
    let mut stmt = conn
        .prepare(
            "SELECT receipt_id, selected_option, selected_option_id, option_labels_json,
                    labels_artifact_id, labels_sha256
             FROM decision_receipts
             WHERE (option_labels_json IS NOT NULL AND option_labels_json != '{}')
                OR (selected_option IS NOT NULL AND selected_option != ''
                    AND (selected_option_id IS NULL OR selected_option_id = ''))",
        )
        .map_err(|e| e.to_string())?;
    let rows: Vec<LegacyReceipt> = stmt
        .query_map([], |row| {
            Ok(LegacyReceipt {
                receipt_id: row.get(0)?,
                selected_option: row.get(1)?,
                selected_option_id: row.get(2)?,
                labels_json: row
                    .get::<_, Option<String>>(3)?
                    .unwrap_or_else(|| "{}".into()),
                labels_id: row.get(4)?,
                labels_sha: row.get(5)?,
            })
        })
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    drop(stmt);
    for row in rows {
        let labels: Value = parse_json(&row.labels_json).unwrap_or(json!({}));
        let mut artifact_id = row.labels_id;
        let mut artifact_sha = row.labels_sha;
        if labels.as_object().map(|o| !o.is_empty()).unwrap_or(false)
            && (artifact_id.is_none() || artifact_sha.is_none())
        {
            let plain = serde_json::to_vec(&labels).map_err(|e| e.to_string())?;
            let put = crate::artifacts::put_plain_bytes(&plain)?;
            artifact_id = Some(put.artifact_id);
            artifact_sha = Some(put.sha256);
        }
        let selected_id = resolve_selected_option_id(
            row.selected_option.as_deref(),
            row.selected_option_id.as_deref(),
            &labels,
        );
        conn.execute(
            "UPDATE decision_receipts
             SET labels_artifact_id=?1, labels_sha256=?2, option_labels_json='{}',
                 selected_option=?3, selected_option_id=?4
             WHERE receipt_id=?5",
            params![
                artifact_id,
                artifact_sha,
                selected_id,
                selected_id,
                row.receipt_id
            ],
        )
        .map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn resolve_selected_option_id(
    selected_option: Option<&str>,
    selected_option_id: Option<&str>,
    labels: &Value,
) -> Option<String> {
    if let Some(id) = selected_option_id.filter(|s| !s.is_empty()) {
        return Some(id.to_string());
    }
    let selected = selected_option.filter(|s| !s.is_empty())?;
    if let Some(obj) = labels.as_object() {
        if obj.contains_key(selected) {
            return Some(selected.to_string());
        }
        for (id, label) in obj {
            if label.as_str() == Some(selected) {
                return Some(id.clone());
            }
        }
    }
    Some(selected.to_string())
}

fn migrate_legacy_candidates(conn: &Connection) -> Result<(), String> {
    let mut stmt = conn
        .prepare(
            "SELECT candidate_id, because FROM expansion_candidates
             WHERE because IS NOT NULL AND because != ''
               AND (because_artifact_id IS NULL OR because_artifact_id = '')",
        )
        .map_err(|e| e.to_string())?;
    let rows: Vec<(String, String)> = stmt
        .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    drop(stmt);
    for (candidate_id, because) in rows {
        let put = crate::artifacts::put_plain_bytes(because.as_bytes())?;
        conn.execute(
            "UPDATE expansion_candidates
             SET because_artifact_id=?1, because_sha256=?2, because=''
             WHERE candidate_id=?3",
            params![put.artifact_id, put.sha256, candidate_id],
        )
        .map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn migrate_legacy_reviews(conn: &Connection) -> Result<(), String> {
    let mut stmt = conn
        .prepare(
            "SELECT review_id, findings_json FROM review_runs
             WHERE findings_json IS NOT NULL AND findings_json != '[]'
               AND (findings_artifact_id IS NULL OR findings_artifact_id = '')",
        )
        .map_err(|e| e.to_string())?;
    let rows: Vec<(String, String)> = stmt
        .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    drop(stmt);
    for (review_id, findings_json) in rows {
        let findings: Value = parse_json(&findings_json).unwrap_or(json!([]));
        if findings.as_array().map(|a| a.is_empty()).unwrap_or(true) {
            conn.execute(
                "UPDATE review_runs SET findings_json='[]' WHERE review_id=?1",
                params![review_id],
            )
            .map_err(|e| e.to_string())?;
            continue;
        }
        let plain = serde_json::to_vec(&findings).map_err(|e| e.to_string())?;
        let put = crate::artifacts::put_plain_bytes(&plain)?;
        conn.execute(
            "UPDATE review_runs
             SET findings_artifact_id=?1, findings_sha256=?2, findings_json='[]'
             WHERE review_id=?3",
            params![put.artifact_id, put.sha256, review_id],
        )
        .map_err(|e| e.to_string())?;
    }
    Ok(())
}

#[tauri::command]
pub fn store_execute(state: tauri::State<'_, Mutex<StateDb>>, op: Value) -> Result<Value, String> {
    let mut db = state.lock().map_err(|error| error.to_string())?;
    db.execute(&op)
}

fn dispatch(conn: &Connection, op: &Value) -> Result<Value, String> {
    match op.get("op").and_then(|value| value.as_str()).unwrap_or("") {
        "ensure_session" => ensure_session(conn, op),
        "set_listening" => set_listening(conn, op),
        "get_listening" => get_listening(conn, op),
        "get_hosted_processing" => get_hosted_processing(conn),
        "set_hosted_processing" => set_hosted_processing(conn, op),
        "persist_final_source" => persist_final_source(conn, op),
        "create_case" => create_case(conn, op),
        "get_case" => get_case(conn, &req_str(op, "caseId")?),
        "update_case" => update_case(conn, op),
        "append_case_event" => {
            append_case_event(
                conn,
                &req_str(op, "caseId")?,
                req_i64(op, "caseVersion")?,
                &req_str(op, "type")?,
                &req_str(op, "at")?,
                op.get("payload").unwrap_or(&Value::Null),
            )?;
            Ok(Value::Null)
        }
        "list_active_cases" => list_active_cases(conn),
        "list_feed_items" => list_feed_items(conn),
        "add_feed_item" => add_feed_item(conn, op),
        "list_source_segments" => list_source_segments(conn, op),
        "enqueue" => enqueue(conn, op),
        "claim_next" => claim_next(conn, op),
        "complete" => {
            conn.execute(
                "DELETE FROM work_items WHERE work_id = ?1",
                params![req_str(op, "workId")?],
            )
            .map_err(|error| error.to_string())?;
            Ok(Value::Null)
        }
        "requeue" => requeue(conn, op),
        "upsert_judgment" => upsert_judgment(conn, op),
        "find_completed_judgment" => find_completed_judgment(conn, op),
        "append_domain_event" => append_domain_event(conn, op),
        "list_domain_events" => list_domain_events(conn, op),
        "count_work_items" => count_work_items(conn),
        "dead_letter" => dead_letter(conn, op),
        "list_dead_letters" => list_dead_letters(conn),
        "upsert_judgment_attempt" => upsert_judgment_attempt(conn, op),
        "put_memory" => put_memory(conn, op),
        "delete_memory" => delete_memory(conn, op),
        "get_memory" => get_memory(conn, op),
        "list_memories" => list_memories(conn),
        "open_session" => open_session(conn, op),
        "close_session" => close_session(conn, op),
        "current_session" => current_session(conn),
        "list_sessions" => list_sessions(conn),
        "put_episode" => put_episode(conn, op),
        "record_completed_episode" => record_completed_episode(conn, op),
        "list_episodes" => list_episodes(conn),
        "put_receipt" => put_receipt(conn, op),
        "list_receipts" => list_receipts(conn),
        "put_pattern" => put_pattern(conn, op),
        "get_pattern" => get_pattern(conn, op),
        "list_patterns" => list_patterns(conn),
        "put_candidate" => put_candidate(conn, op),
        "list_candidates" => list_candidates(conn),
        "put_pattern_evidence" => put_pattern_evidence(conn, op),
        "list_pattern_evidence" => list_pattern_evidence(conn, op),
        "put_candidate_event" => put_candidate_event(conn, op),
        "get_candidate_event" => get_candidate_event(conn, op),
        "list_candidate_events" => list_candidate_events(conn, op),
        "update_candidate_event_status" => update_candidate_event_status(conn, op),
        "put_ambient_suppression" => put_ambient_suppression(conn, op),
        "is_ambient_suppressed" => is_ambient_suppressed(conn, op),
        "save_hosted_grant"
        | "revoke_hosted_grant"
        | "find_hosted_grant"
        | "read_hosted_grant"
        | "reserve_hosted_grant"
        | "commit_hosted_grant"
        | "release_hosted_grant"
        | "release_uncommitted_hosted_grants" => crate::grants::dispatch(
            conn,
            op.get("op").and_then(|value| value.as_str()).unwrap_or(""),
            op,
        )
        .unwrap_or(Err("unknown_op".into())),
        "put_review" => put_review(conn, op),
        "list_reviews" => list_reviews(conn),
        "compact" => compact(conn, op),
        "foundation_put" => foundation_put(conn, op),
        "foundation_claim" => foundation_claim(conn, op),
        "foundation_get" => foundation_get(conn, op),
        "foundation_list" => foundation_list(conn, op),
        "link_execution_case" => link_execution_case(conn, op),
        "list_execution_cases" => list_execution_cases(conn, op),
        _ => Err("unknown_op".into()),
    }
}

fn ensure_session(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "INSERT OR IGNORE INTO sessions(session_id, created_at, listening) VALUES (?1, ?2, 0)",
        params![req_str(op, "sessionId")?, req_str(op, "createdAt")?],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn set_listening(conn: &Connection, op: &Value) -> Result<Value, String> {
    let listening = op
        .get("listening")
        .and_then(|value| value.as_bool())
        .ok_or("missing_listening")?;
    conn.execute(
        "UPDATE sessions SET listening = ?1 WHERE session_id = ?2",
        params![if listening { 1 } else { 0 }, req_str(op, "sessionId")?],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn get_listening(conn: &Connection, op: &Value) -> Result<Value, String> {
    let listening = conn
        .query_row(
            "SELECT listening FROM sessions WHERE session_id = ?1",
            params![req_str(op, "sessionId")?],
            |row| row.get::<_, i64>(0),
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(json!(listening == Some(1)))
}

fn get_hosted_processing(conn: &Connection) -> Result<Value, String> {
    let value: Option<String> = conn
        .query_row(
            "SELECT value_json FROM app_settings WHERE key = ?1",
            params!["hosted_processing_enabled"],
            |row| row.get(0),
        )
        .optional()
        .map_err(|error| error.to_string())?;
    let enabled = value
        .as_deref()
        .and_then(|raw| serde_json::from_str::<bool>(raw).ok())
        .unwrap_or(false);
    Ok(json!(enabled))
}

fn set_hosted_processing(conn: &Connection, op: &Value) -> Result<Value, String> {
    let enabled = op
        .get("enabled")
        .and_then(|value| value.as_bool())
        .ok_or("missing_enabled")?;
    conn.execute(
        "INSERT INTO app_settings(key, value_json, updated_at) VALUES (?1, ?2, ?3)
         ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at",
        params![
            "hosted_processing_enabled",
            serde_json::to_string(&enabled).map_err(|e| e.to_string())?,
            now_iso()
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn persist_final_source(conn: &Connection, op: &Value) -> Result<Value, String> {
    let event = req_obj(op, "event")?;
    let segment = req_obj(event, "segment")?;
    let changed = conn
        .execute(
            "INSERT OR IGNORE INTO source_events(
              source_event_id, session_id, segment_id, revision, sequence, origin, speaker_key,
              start_ms, end_ms, final, text_artifact_id, text_sha256, policy_json, created_at
            ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, 1, ?10, ?11, ?12, ?13)",
            params![
                req_str(event, "sourceEventId")?,
                req_str(event, "sessionId")?,
                req_str(segment, "segmentId")?,
                req_i64(segment, "revision")?,
                req_i64(segment, "sequence")?,
                req_str(segment, "origin")?,
                opt_str(segment, "speakerKey"),
                req_i64(segment, "startMs")?,
                req_i64(segment, "endMs")?,
                req_str(event, "textArtifactId")?,
                req_str(event, "textSha256")?,
                json_text(event.get("policy").unwrap_or(&Value::Null))?,
                req_str(event, "createdAt")?,
            ],
        )
        .map_err(|error| error.to_string())?;
    Ok(json!({ "inserted": changed > 0 }))
}

fn create_case(conn: &Connection, op: &Value) -> Result<Value, String> {
    let case_id = req_str(op, "caseId")?;
    let at = req_str(op, "at")?;
    conn.execute(
        "INSERT INTO cases(
          case_id, version, origin, kind, status, phase, priority, parent_case_id, created_at, updated_at
        ) VALUES (?1, 1, ?2, ?3, 'active', 'intake', ?4, ?5, ?6, ?6)",
        params![
            case_id,
            req_str(op, "origin")?,
            req_str(op, "kind")?,
            req_i64(op, "priority")?,
            opt_str(op, "parentCaseId"),
            at,
        ],
    )
    .map_err(|error| error.to_string())?;
    conn.execute(
        "INSERT OR IGNORE INTO executions(
          execution_id, version, origin, kind, status, phase, priority, parent_execution_id, created_at, updated_at
        ) VALUES (?1, 1, ?2, ?3, 'active', 'intake', ?4, ?5, ?6, ?6)",
        params![
            case_id,
            req_str(op, "origin")?,
            req_str(op, "kind")?,
            req_i64(op, "priority")?,
            opt_str(op, "parentCaseId"),
            at,
        ],
    )
    .map_err(|error| error.to_string())?;
    append_case_event(
        conn,
        &case_id,
        1,
        "case.created",
        &at,
        &json!({ "origin": req_str(op, "origin")?, "kind": req_str(op, "kind")? }),
    )?;
    get_case(conn, &case_id)
}

fn get_case(conn: &Connection, case_id: &str) -> Result<Value, String> {
    let row = conn
        .query_row(
            "SELECT * FROM cases WHERE case_id = ?1",
            params![case_id],
            map_case,
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(row.unwrap_or(Value::Null))
}

fn update_case(conn: &Connection, op: &Value) -> Result<Value, String> {
    let case_id = req_str(op, "caseId")?;
    let expected = req_i64(op, "expectedVersion")?;
    let current = get_case(conn, &case_id)?;
    if current.is_null()
        || current.get("version").and_then(|value| value.as_i64()) != Some(expected)
    {
        return Ok(Value::Null);
    }
    let patch = req_obj(op, "patch")?;
    let wait_kind = if patch.get("waitKind").is_some() {
        opt_str(patch, "waitKind")
    } else {
        opt_str(&current, "waitKind")
    };
    conn.execute(
        "UPDATE cases SET version = ?1, status = COALESCE(?2, status), phase = COALESCE(?3, phase), wait_kind = ?4, updated_at = ?5
         WHERE case_id = ?6 AND version = ?7",
        params![
            expected + 1,
            opt_str(patch, "status"),
            opt_str(patch, "phase"),
            wait_kind,
            req_str(patch, "at")?,
            case_id,
            expected,
        ],
    )
    .map_err(|error| error.to_string())?;
    get_case(conn, &case_id)
}

fn append_case_event(
    conn: &Connection,
    case_id: &str,
    case_version: i64,
    event_type: &str,
    at: &str,
    payload: &Value,
) -> Result<(), String> {
    conn.execute(
        "INSERT INTO case_events(event_id, case_id, case_version, type, at, payload_json) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![
            format!("{case_id}:{case_version}:{event_type}:{at}"),
            case_id,
            case_version,
            event_type,
            at,
            json_text(payload)?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(())
}

fn list_active_cases(conn: &Connection) -> Result<Value, String> {
    query_values(
        conn,
        "SELECT * FROM cases WHERE status IN ('active','waiting','blocked','failed') ORDER BY priority DESC, created_at",
        params![],
        map_case,
    )
}

fn list_feed_items(conn: &Connection) -> Result<Value, String> {
    let mut stmt = conn
        .prepare(
            "SELECT item_id, kind, content_artifact_id, content_sha256, created_at, case_id
             FROM feed_items ORDER BY created_at",
        )
        .map_err(|error| error.to_string())?;
    let rows = stmt
        .query_map([], |row| {
            Ok(json!({
                "itemId": row.get::<_, String>(0)?,
                "kind": row.get::<_, String>(1)?,
                "contentArtifactId": row.get::<_, String>(2)?,
                "contentSha256": row.get::<_, String>(3)?,
                "createdAt": row.get::<_, String>(4)?,
                "caseId": row.get::<_, Option<String>>(5)?,
            }))
        })
        .map_err(|error| error.to_string())?;
    let mut items = Vec::new();
    for row in rows {
        let mut value = row.map_err(|error| error.to_string())?;
        if value.get("caseId").and_then(|v| v.as_str()).is_none() {
            if let Some(obj) = value.as_object_mut() {
                obj.remove("caseId");
            }
        }
        items.push(value);
    }
    Ok(Value::Array(items))
}

fn add_feed_item(conn: &Connection, op: &Value) -> Result<Value, String> {
    let item = req_obj(op, "item")?;
    conn.execute(
        "INSERT OR IGNORE INTO feed_items(item_id, kind, content_artifact_id, content_sha256, created_at, case_id)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![
            req_str(item, "itemId")?,
            req_str(item, "kind")?,
            req_str(item, "contentArtifactId")?,
            req_str(item, "contentSha256")?,
            req_str(item, "createdAt")?,
            item.get("caseId").and_then(|v| v.as_str()),
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn list_source_segments(conn: &Connection, op: &Value) -> Result<Value, String> {
    let mut stmt = conn
        .prepare(
            "SELECT segment_id, speaker_key, sequence, origin, final, text_artifact_id
             FROM source_events WHERE session_id = ?1 ORDER BY sequence",
        )
        .map_err(|error| error.to_string())?;
    let rows = stmt
        .query_map(params![req_str(op, "sessionId")?], |row| {
            let artifact: String = row.get(5)?;
            Ok(json!({
                "segmentId": row.get::<_, String>(0)?,
                "speakerKey": row.get::<_, Option<String>>(1)?,
                "text": format!("[artifact:{artifact}]"),
                "final": row.get::<_, i64>(4)? == 1,
                "origin": row.get::<_, String>(3)?,
                "sequence": row.get::<_, i64>(2)?,
            }))
        })
        .map_err(|error| error.to_string())?;
    let mut items = Vec::new();
    for row in rows {
        items.push(row.map_err(|error| error.to_string())?);
    }
    Ok(Value::Array(items))
}

fn enqueue(conn: &Connection, op: &Value) -> Result<Value, String> {
    let item = req_obj(op, "item")?;
    conn.execute(
        "INSERT OR IGNORE INTO work_items(work_id, type, priority, available_at, payload_json, created_at, parent_work_id, correlation_id) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
        params![
            req_str(item, "workId")?,
            req_str(item, "type")?,
            req_i64(item, "priority")?,
            req_str(item, "availableAt")?,
            json_text(item.get("payload").unwrap_or(&json!({})))?,
            req_str(item, "createdAt")?,
            opt_str(item, "parentWorkId"),
            opt_str(item, "correlationId"),
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn claim_next(conn: &Connection, op: &Value) -> Result<Value, String> {
    let now = req_str(op, "now")?;
    let row = conn
        .query_row(
            "SELECT work_id, type, priority, available_at, payload_json, created_at, parent_work_id, correlation_id FROM work_items
             WHERE available_at <= ?1 AND (lease_until IS NULL OR lease_until < ?1)
             ORDER BY priority DESC, created_at LIMIT 1",
            params![now],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, Option<String>>(6)?,
                    row.get::<_, Option<String>>(7)?,
                ))
            },
        )
        .optional()
        .map_err(|error| error.to_string())?;
    let Some((
        work_id,
        kind,
        priority,
        available_at,
        payload,
        created_at,
        parent_work_id,
        correlation_id,
    )) = row
    else {
        return Ok(Value::Null);
    };
    let lease_until = shift_iso(&now, req_i64(op, "leaseMs")?)?;
    let changed = conn
        .execute(
            "UPDATE work_items SET lease_owner = ?1, lease_until = ?2 WHERE work_id = ?3 AND (lease_until IS NULL OR lease_until < ?4)",
            params![req_str(op, "owner")?, lease_until, work_id, now],
        )
        .map_err(|error| error.to_string())?;
    if changed == 0 {
        return Ok(Value::Null);
    }
    let mut value = json!({
        "workId": work_id,
        "type": kind,
        "priority": priority,
        "availableAt": available_at,
        "payload": parse_json(&payload)?,
        "createdAt": created_at,
    });
    if let Some(parent) = parent_work_id {
        value
            .as_object_mut()
            .ok_or_else(|| "object".to_string())?
            .insert("parentWorkId".into(), json!(parent));
    }
    if let Some(correlation) = correlation_id {
        value
            .as_object_mut()
            .ok_or_else(|| "object".to_string())?
            .insert("correlationId".into(), json!(correlation));
    }
    Ok(value)
}

fn requeue(conn: &Connection, op: &Value) -> Result<Value, String> {
    if let Some(payload) = op.get("payload") {
        conn.execute(
            "UPDATE work_items SET available_at = ?1, payload_json = ?2, lease_owner = NULL, lease_until = NULL WHERE work_id = ?3",
            params![req_str(op, "availableAt")?, json_text(payload)?, req_str(op, "workId")?],
        )
        .map_err(|error| error.to_string())?;
    } else {
        conn.execute(
            "UPDATE work_items SET available_at = ?1, lease_owner = NULL, lease_until = NULL WHERE work_id = ?2",
            params![req_str(op, "availableAt")?, req_str(op, "workId")?],
        )
        .map_err(|error| error.to_string())?;
    }
    Ok(Value::Null)
}

fn upsert_judgment(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT INTO judgments(
          judgment_id, provider, question_set_id, question_set_version, model, status,
          case_id, case_version, request_artifact_id, request_hash, response_artifact_id,
          response_hash, failure_category, input_tokens, output_tokens, elapsed_ms,
          created_at, completed_at
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18)
        ON CONFLICT(judgment_id) DO UPDATE SET
          status=excluded.status,
          response_artifact_id=excluded.response_artifact_id,
          response_hash=excluded.response_hash,
          failure_category=excluded.failure_category,
          input_tokens=excluded.input_tokens,
          output_tokens=excluded.output_tokens,
          elapsed_ms=excluded.elapsed_ms,
          completed_at=excluded.completed_at",
        params![
            req_str(record, "judgmentId")?,
            opt_str(record, "provider"),
            req_str(record, "questionSetId")?,
            req_str(record, "questionSetVersion")?,
            req_str(record, "model")?,
            req_str(record, "status")?,
            opt_str(record, "caseId"),
            opt_i64(record, "caseVersion"),
            opt_str(record, "requestArtifactId"),
            opt_str(record, "requestHash"),
            opt_str(record, "responseArtifactId"),
            opt_str(record, "responseHash"),
            opt_str(record, "failureCategory"),
            opt_i64(record, "inputTokens"),
            opt_i64(record, "outputTokens"),
            opt_i64(record, "elapsedMs"),
            req_str(record, "createdAt")?,
            opt_str(record, "completedAt"),
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn find_completed_judgment(conn: &Connection, op: &Value) -> Result<Value, String> {
    let row = conn
        .query_row(
            "SELECT * FROM judgments WHERE request_hash = ?1 AND status = 'completed' LIMIT 1",
            params![req_str(op, "requestHash")?],
            map_judgment,
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(row.unwrap_or(Value::Null))
}

fn append_domain_event(conn: &Connection, op: &Value) -> Result<Value, String> {
    append_domain_event_raw(
        conn,
        &req_str(op, "type")?,
        &req_str(op, "at")?,
        op.get("payload").unwrap_or(&json!({})),
    )
}

fn append_domain_event_raw(
    conn: &Connection,
    event_type: &str,
    at: &str,
    payload: &Value,
) -> Result<Value, String> {
    conn.execute(
        "INSERT INTO domain_events(type, at, payload_json) VALUES (?1, ?2, ?3)",
        params![event_type, at, json_text(payload)?],
    )
    .map_err(|error| error.to_string())?;
    Ok(json!(conn.last_insert_rowid()))
}

fn list_domain_events(conn: &Connection, op: &Value) -> Result<Value, String> {
    let limit = req_i64(op, "limit")?.max(0);
    let mut stmt = conn
        .prepare("SELECT sequence, type, at, payload_json FROM domain_events ORDER BY sequence DESC LIMIT ?1")
        .map_err(|error| error.to_string())?;
    let rows = stmt
        .query_map(params![limit], |row| {
            Ok(json!({
                "sequence": row.get::<_, i64>(0)?,
                "type": row.get::<_, String>(1)?,
                "at": row.get::<_, String>(2)?,
                "payload": parse_json(&row.get::<_, String>(3)?).unwrap_or(Value::Null),
            }))
        })
        .map_err(|error| error.to_string())?;
    let mut items = Vec::new();
    for row in rows {
        items.push(row.map_err(|error| error.to_string())?);
    }
    items.reverse();
    Ok(Value::Array(items))
}

fn count_work_items(conn: &Connection) -> Result<Value, String> {
    let count: i64 = conn
        .query_row("SELECT COUNT(*) FROM work_items", [], |row| row.get(0))
        .map_err(|error| error.to_string())?;
    Ok(json!(count))
}

fn dead_letter(conn: &Connection, op: &Value) -> Result<Value, String> {
    let work_id = req_str(op, "workId")?;
    conn.execute(
        "DELETE FROM work_items WHERE work_id = ?1",
        params![work_id],
    )
    .map_err(|error| error.to_string())?;
    conn.execute(
        "INSERT OR REPLACE INTO dead_letters(work_id, reason_code, at) VALUES (?1, ?2, ?3)",
        params![work_id, req_str(op, "reasonCode")?, req_str(op, "at")?],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn list_dead_letters(conn: &Connection) -> Result<Value, String> {
    query_values(
        conn,
        "SELECT work_id, reason_code, at FROM dead_letters",
        params![],
        |row| {
            Ok(json!({
                "workId": row.get::<_, String>(0)?,
                "reasonCode": row.get::<_, String>(1)?,
                "at": row.get::<_, String>(2)?,
            }))
        },
    )
}

fn upsert_judgment_attempt(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT INTO judgment_attempts(
          attempt_id, case_id, work_id, attempt, max_attempts, next_attempt_at, failure_category, provider_request_id, created_at
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
        ON CONFLICT(attempt_id) DO UPDATE SET
          attempt=excluded.attempt,
          next_attempt_at=excluded.next_attempt_at,
          failure_category=excluded.failure_category,
          provider_request_id=excluded.provider_request_id",
        params![
            req_str(record, "attemptId")?,
            req_str(record, "caseId")?,
            opt_str(record, "workId"),
            req_i64(record, "attempt")?,
            req_i64(record, "maxAttempts")?,
            opt_str(record, "nextAttemptAt"),
            opt_str(record, "failureCategory"),
            opt_str(record, "providerRequestId"),
            req_str(record, "createdAt")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn put_memory(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    let content_artifact_id = opt_str(record, "contentArtifactId");
    let content_sha256 = opt_str(record, "contentSha256");
    let empty_metadata = json!({});
    let metadata = record.get("metadata").unwrap_or(&empty_metadata);
    // Free prose must not land in value_json; TS adapter packs into artifacts first.
    conn.execute(
        "INSERT INTO memories(
           memory_id, kind, key, value_json, source, created_at,
           content_artifact_id, content_sha256, metadata_json
         ) VALUES (?1, ?2, ?3, '{}', ?4, ?5, ?6, ?7, ?8)
         ON CONFLICT(kind, key) DO UPDATE SET
           memory_id=excluded.memory_id,
           value_json='{}',
           source=excluded.source,
           content_artifact_id=excluded.content_artifact_id,
           content_sha256=excluded.content_sha256,
           metadata_json=excluded.metadata_json",
        params![
            req_str(record, "memoryId")?,
            req_str(record, "kind")?,
            req_str(record, "key")?,
            req_str(record, "source")?,
            req_str(record, "createdAt")?,
            content_artifact_id,
            content_sha256,
            json_text(metadata)?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn delete_memory(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "DELETE FROM memories WHERE kind = ?1 AND key = ?2",
        params![req_str(op, "kind")?, req_str(op, "key")?],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn get_memory(conn: &Connection, op: &Value) -> Result<Value, String> {
    let row = conn
        .query_row(
            "SELECT * FROM memories WHERE kind = ?1 AND key = ?2",
            params![req_str(op, "kind")?, req_str(op, "key")?],
            map_memory,
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(row.unwrap_or(Value::Null))
}

fn list_memories(conn: &Connection) -> Result<Value, String> {
    query_values(conn, "SELECT * FROM memories", params![], map_memory)
}

fn open_session(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT OR REPLACE INTO work_sessions(session_id, started_at, ended_at, termination, episode_count) VALUES (?1, ?2, ?3, ?4, ?5)",
        params![
            req_str(record, "sessionId")?,
            req_str(record, "startedAt")?,
            opt_str(record, "endedAt"),
            req_str(record, "termination")?,
            req_i64(record, "episodeCount")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn close_session(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "UPDATE work_sessions SET ended_at = ?1, termination = ?2, episode_count = ?3 WHERE session_id = ?4",
        params![
            req_str(op, "endedAt")?,
            req_str(op, "termination")?,
            req_i64(op, "episodeCount")?,
            req_str(op, "sessionId")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn current_session(conn: &Connection) -> Result<Value, String> {
    let row = conn
        .query_row(
            "SELECT * FROM work_sessions WHERE termination = 'open' ORDER BY started_at DESC LIMIT 1",
            [],
            map_session,
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(row.unwrap_or(Value::Null))
}

fn list_sessions(conn: &Connection) -> Result<Value, String> {
    query_values(conn, "SELECT * FROM work_sessions", params![], map_session)
}

fn put_episode(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT OR IGNORE INTO work_episodes(episode_id, session_id, case_id, signature, outcome, started_at, completed_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        params![
            req_str(record, "episodeId")?,
            req_str(record, "sessionId")?,
            opt_str(record, "caseId"),
            req_str(record, "signature")?,
            req_str(record, "outcome")?,
            req_str(record, "startedAt")?,
            opt_str(record, "completedAt"),
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn record_completed_episode(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT OR IGNORE INTO work_episodes(episode_id, session_id, case_id, signature, outcome, started_at, completed_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        params![
            req_str(record, "episodeId")?,
            req_str(record, "sessionId")?,
            opt_str(record, "caseId"),
            req_str(record, "signature")?,
            req_str(record, "outcome")?,
            req_str(record, "startedAt")?,
            opt_str(record, "completedAt"),
        ],
    )
    .map_err(|error| error.to_string())?;
    let outcome = req_str(record, "outcome")?;
    if outcome != "completed" {
        return Ok(Value::Null);
    }
    let signature = req_str(record, "signature")?;
    let episode_id = req_str(record, "episodeId")?;
    let session_id = req_str(record, "sessionId")?;
    let started_at = req_str(record, "startedAt")?;
    let completed_at = opt_str(record, "completedAt").unwrap_or_else(|| started_at.clone());
    let existing = conn
        .query_row(
            "SELECT signature, count, session_ids_json, outcomes_json, first_at, last_at, evidence_ids_json
             FROM pattern_evidence WHERE signature = ?1",
            params![signature],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, String>(6)?,
                ))
            },
        )
        .optional()
        .map_err(|error| error.to_string())?;

    let mut prior_count: i64 = 0;
    let mut session_ids: Vec<String> = Vec::new();
    let mut outcomes: serde_json::Map<String, Value> = serde_json::Map::new();
    let mut first_at_existing: Option<String> = None;
    let mut evidence_ids: Vec<String> = Vec::new();
    let mut existing_json: Option<Value> = None;
    if let Some(row) = existing {
        prior_count = row.1;
        session_ids = serde_json::from_str(&row.2).unwrap_or_default();
        outcomes = serde_json::from_str(&row.3).unwrap_or_default();
        first_at_existing = Some(row.4.clone());
        evidence_ids = serde_json::from_str(&row.6).unwrap_or_default();
        existing_json = Some(json!({
            "signature": row.0,
            "count": row.1,
            "sessionIds": serde_json::from_str::<Value>(&row.2).unwrap_or(json!([])),
            "outcomes": serde_json::from_str::<Value>(&row.3).unwrap_or(json!({})),
            "firstAt": row.4,
            "lastAt": row.5,
            "evidenceIds": serde_json::from_str::<Value>(&row.6).unwrap_or(json!([])),
        }));
    }
    if evidence_ids.iter().any(|id| id == &episode_id) {
        return Ok(existing_json.unwrap_or(Value::Null));
    }
    if !session_ids.iter().any(|id| id == &session_id) {
        session_ids.push(session_id);
    }
    let prior = outcomes.get(&outcome).and_then(|v| v.as_i64()).unwrap_or(0);
    outcomes.insert(outcome, json!(prior + 1));
    evidence_ids.push(episode_id);
    let count = prior_count + 1;
    let first_at = first_at_existing.unwrap_or_else(|| completed_at.clone());
    conn.execute(
        "INSERT OR REPLACE INTO pattern_evidence(
          signature, count, session_ids_json, outcomes_json, first_at, last_at, evidence_ids_json
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        params![
            signature,
            count,
            serde_json::to_string(&session_ids).unwrap_or_else(|_| "[]".into()),
            Value::Object(outcomes.clone()).to_string(),
            first_at,
            completed_at,
            serde_json::to_string(&evidence_ids).unwrap_or_else(|_| "[]".into()),
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(json!({
        "signature": signature,
        "count": count,
        "sessionIds": session_ids,
        "outcomes": Value::Object(outcomes),
        "firstAt": first_at,
        "lastAt": completed_at,
        "evidenceIds": evidence_ids,
    }))
}

fn list_episodes(conn: &Connection) -> Result<Value, String> {
    query_values(conn, "SELECT * FROM work_episodes", params![], map_episode)
}

fn put_receipt(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    let receipt_id = req_str(record, "receiptId")?;
    let decision_id = opt_str(record, "decisionId").unwrap_or_else(|| receipt_id.clone());
    let selected_option_id =
        opt_str(record, "selectedOptionId").or_else(|| opt_str(record, "selectedOption"));
    conn.execute(
        "INSERT INTO decision_receipts(
          receipt_id, case_id, gate_id, policy_version, question_type, provider,
          probabilities_json, thresholds_json, selected_option, result, reason_code,
          latency_ms, retries, created_at,
          decision_id, judgment_id, reflex_id, selected_option_id, option_labels_json,
          requested_at, completed_at, labels_artifact_id, labels_sha256
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, '{}', ?19, ?20, ?21, ?22)",
        params![
            receipt_id,
            opt_str(record, "caseId"),
            req_str(record, "gateId")?,
            req_str(record, "policyVersion")?,
            req_str(record, "questionType")?,
            req_str(record, "provider")?,
            json_text(record.get("probabilities").unwrap_or(&json!({})))?,
            json_text(record.get("thresholds").unwrap_or(&json!({})))?,
            selected_option_id.clone(),
            req_str(record, "result")?,
            req_str(record, "reasonCode")?,
            opt_i64(record, "latencyMs"),
            req_i64(record, "retries")?,
            req_str(record, "createdAt")?,
            decision_id,
            opt_str(record, "judgmentId"),
            opt_str(record, "reflexId"),
            selected_option_id,
            opt_str(record, "requestedAt"),
            opt_str(record, "completedAt"),
            opt_str(record, "labelsArtifactId"),
            opt_str(record, "labelsSha256"),
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn list_receipts(conn: &Connection) -> Result<Value, String> {
    query_values(
        conn,
        "SELECT * FROM decision_receipts ORDER BY created_at",
        params![],
        map_receipt,
    )
}

fn put_pattern(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT OR REPLACE INTO pattern_evidence(
          signature, count, session_ids_json, outcomes_json, first_at, last_at, evidence_ids_json
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        params![
            req_str(record, "signature")?,
            req_i64(record, "count")?,
            json_text(record.get("sessionIds").unwrap_or(&json!([])))?,
            json_text(record.get("outcomes").unwrap_or(&json!({})))?,
            req_str(record, "firstAt")?,
            req_str(record, "lastAt")?,
            json_text(record.get("evidenceIds").unwrap_or(&json!([])))?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn get_pattern(conn: &Connection, op: &Value) -> Result<Value, String> {
    let row = conn
        .query_row(
            "SELECT * FROM pattern_evidence WHERE signature = ?1",
            params![req_str(op, "signature")?],
            map_pattern,
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(row.unwrap_or(Value::Null))
}

fn list_patterns(conn: &Connection) -> Result<Value, String> {
    query_values(
        conn,
        "SELECT * FROM pattern_evidence",
        params![],
        map_pattern,
    )
}

fn put_candidate(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    let meta_json = record
        .get("meta")
        .map(|value| serde_json::to_string(value).map_err(|e| e.to_string()))
        .transpose()?;
    conn.execute(
        "INSERT OR REPLACE INTO expansion_candidates(
           candidate_id, signature, state, because, needed, updated_at,
           because_artifact_id, because_sha256, meta_json
         ) VALUES (?1, ?2, ?3, '', ?4, ?5, ?6, ?7, ?8)",
        params![
            req_str(record, "candidateId")?,
            req_str(record, "signature")?,
            req_str(record, "state")?,
            req_str(record, "needed")?,
            req_str(record, "updatedAt")?,
            opt_str(record, "becauseArtifactId"),
            opt_str(record, "becauseSha256"),
            meta_json,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn list_candidates(conn: &Connection) -> Result<Value, String> {
    query_values(
        conn,
        "SELECT * FROM expansion_candidates",
        params![],
        map_candidate,
    )
}

fn put_pattern_evidence(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT OR IGNORE INTO pattern_evidence_events(
          evidence_id, signature, source_class, route_or_tool, user_action, outcome_class,
          duplicate_count, time_to_action_ms, feedback, case_id, created_at
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
        params![
            req_str(record, "evidenceId")?,
            req_str(record, "signature")?,
            req_str(record, "sourceClass")?,
            opt_str(record, "routeOrTool"),
            req_str(record, "userAction")?,
            req_str(record, "outcomeClass")?,
            record
                .get("duplicateCount")
                .and_then(|v| v.as_i64())
                .unwrap_or(0),
            record.get("timeToActionMs").and_then(|v| v.as_i64()),
            opt_str(record, "feedback"),
            opt_str(record, "caseId"),
            req_str(record, "createdAt")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn list_pattern_evidence(conn: &Connection, op: &Value) -> Result<Value, String> {
    if let Some(signature) = op.get("signature").and_then(|v| v.as_str()) {
        return query_values(
            conn,
            "SELECT * FROM pattern_evidence_events WHERE signature = ?1 ORDER BY created_at",
            params![signature],
            map_pattern_evidence,
        );
    }
    query_values(
        conn,
        "SELECT * FROM pattern_evidence_events ORDER BY created_at",
        params![],
        map_pattern_evidence,
    )
}

fn put_candidate_event(conn: &Connection, op: &Value) -> Result<Value, String> {
    let event = req_obj(op, "event")?;
    conn.execute(
        "INSERT OR REPLACE INTO candidate_events(
          candidate_event_id, source_event_id, case_id, kind, subject_refs_json, source_slice_refs_json,
          extractor_version, status, urgency_reason, subject_key, created_at, updated_at
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
        params![
            req_str(event, "candidateEventId")?,
            req_str(event, "sourceEventId")?,
            req_str(event, "caseId")?,
            req_str(event, "kind")?,
            json_text(event.get("subjectRefs").unwrap_or(&json!([])))?,
            json_text(event.get("sourceSliceRefs").unwrap_or(&json!([])))?,
            req_str(event, "extractorVersion")?,
            req_str(event, "status")?,
            opt_str(event, "urgencyReason"),
            opt_str(event, "subjectKey"),
            req_str(event, "createdAt")?,
            req_str(event, "updatedAt")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn get_candidate_event(conn: &Connection, op: &Value) -> Result<Value, String> {
    let candidate_event_id = req_str(op, "candidateEventId")?;
    let row = conn
        .query_row(
            "SELECT * FROM candidate_events WHERE candidate_event_id = ?1",
            params![candidate_event_id],
            map_candidate_event,
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(row.unwrap_or(Value::Null))
}

fn list_candidate_events(conn: &Connection, op: &Value) -> Result<Value, String> {
    if let Some(case_id) = opt_str(op, "caseId") {
        return query_values(
            conn,
            "SELECT * FROM candidate_events WHERE case_id = ?1",
            params![case_id],
            map_candidate_event,
        );
    }
    query_values(
        conn,
        "SELECT * FROM candidate_events",
        params![],
        map_candidate_event,
    )
}

fn update_candidate_event_status(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "UPDATE candidate_events SET status = ?1, updated_at = ?2 WHERE candidate_event_id = ?3",
        params![
            req_str(op, "status")?,
            req_str(op, "updatedAt")?,
            req_str(op, "candidateEventId")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn put_ambient_suppression(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "INSERT OR REPLACE INTO ambient_suppressions(suppression_key, reason, created_at) VALUES (?1, ?2, ?3)",
        params![
            req_str(op, "key")?,
            req_str(op, "reason")?,
            req_str(op, "createdAt")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn is_ambient_suppressed(conn: &Connection, op: &Value) -> Result<Value, String> {
    let key = req_str(op, "key")?;
    let exists: Option<i64> = conn
        .query_row(
            "SELECT 1 FROM ambient_suppressions WHERE suppression_key = ?1",
            params![key],
            |row| row.get(0),
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(Value::Bool(exists.is_some()))
}

fn map_candidate_event(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let subject_refs_json = row.get::<_, String>("subject_refs_json")?;
    let source_slice_refs_json = row.get::<_, String>("source_slice_refs_json")?;
    let mut value = json!({
        "candidateEventId": row.get::<_, String>("candidate_event_id")?,
        "sourceEventId": row.get::<_, String>("source_event_id")?,
        "caseId": row.get::<_, String>("case_id")?,
        "kind": row.get::<_, String>("kind")?,
        "subjectRefs": parse_json(&subject_refs_json).unwrap_or(json!([])),
        "sourceSliceRefs": parse_json(&source_slice_refs_json).unwrap_or(json!([])),
        "extractorVersion": row.get::<_, String>("extractor_version")?,
        "status": row.get::<_, String>("status")?,
        "createdAt": row.get::<_, String>("created_at")?,
        "updatedAt": row.get::<_, String>("updated_at")?,
    });
    if let Some(reason) = row.get::<_, Option<String>>("urgency_reason")? {
        value["urgencyReason"] = json!(reason);
    }
    if let Some(key) = row.get::<_, Option<String>>("subject_key")? {
        value["subjectKey"] = json!(key);
    }
    Ok(value)
}

fn put_review(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT OR IGNORE INTO review_runs(
          review_id, trigger_code, at, findings_json,
          sessions_at_review, episodes_at_review, candidates_at_review, built_reflexes_at_review,
          findings_artifact_id, findings_sha256
        ) VALUES (?1, ?2, ?3, '[]', ?4, ?5, ?6, ?7, ?8, ?9)",
        params![
            req_str(record, "reviewId")?,
            req_str(record, "triggerCode")?,
            req_str(record, "at")?,
            req_i64(record, "sessionsAtReview")?,
            req_i64(record, "episodesAtReview")?,
            req_i64(record, "candidatesAtReview")?,
            req_i64(record, "builtReflexesAtReview")?,
            opt_str(record, "findingsArtifactId"),
            opt_str(record, "findingsSha256"),
        ],
    )
    .map_err(|error| error.to_string())?;
    conn.execute(
        "INSERT INTO review_cursors(
          trigger_code, review_id, sessions_at_review, episodes_at_review, candidates_at_review, built_reflexes_at_review
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6)
        ON CONFLICT(trigger_code) DO UPDATE SET
          review_id=excluded.review_id,
          sessions_at_review=excluded.sessions_at_review,
          episodes_at_review=excluded.episodes_at_review,
          candidates_at_review=excluded.candidates_at_review,
          built_reflexes_at_review=excluded.built_reflexes_at_review",
        params![
            req_str(record, "triggerCode")?,
            req_str(record, "reviewId")?,
            req_i64(record, "sessionsAtReview")?,
            req_i64(record, "episodesAtReview")?,
            req_i64(record, "candidatesAtReview")?,
            req_i64(record, "builtReflexesAtReview")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn list_reviews(conn: &Connection) -> Result<Value, String> {
    query_values(conn, "SELECT * FROM review_runs", params![], map_review)
}

fn compact(conn: &Connection, op: &Value) -> Result<Value, String> {
    let cutoff = shift_iso(&req_str(op, "nowIso")?, -RETENTION_MS)?;
    let changed = conn
        .execute(
            "DELETE FROM work_episodes WHERE outcome IN ('abandoned', 'failed') AND completed_at IS NOT NULL AND completed_at < ?1",
            params![cutoff],
        )
        .map_err(|error| error.to_string())?;
    conn.execute("DELETE FROM dead_letters WHERE at < ?1", params![cutoff])
        .map_err(|error| error.to_string())?;
    Ok(json!(changed))
}

fn map_case(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let mut value = json!({
        "caseId": row.get::<_, String>("case_id")?,
        "executionId": row.get::<_, String>("case_id")?,
        "version": row.get::<_, i64>("version")?,
        "origin": row.get::<_, String>("origin")?,
        "kind": row.get::<_, String>("kind")?,
        "status": row.get::<_, String>("status")?,
        "phase": row.get::<_, String>("phase")?,
        "priority": row.get::<_, i64>("priority")?,
        "createdAt": row.get::<_, String>("created_at")?,
        "updatedAt": row.get::<_, String>("updated_at")?,
    });
    if let Some(parent) = row.get::<_, Option<String>>("parent_case_id")? {
        value["parentCaseId"] = json!(parent);
    }
    if let Some(wait) = row.get::<_, Option<String>>("wait_kind")? {
        value["waitKind"] = json!(wait);
    }
    Ok(value)
}

fn map_memory(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let metadata: String = row
        .get::<_, Option<String>>("metadata_json")?
        .unwrap_or_else(|| "{}".into());
    let mut value = json!({
        "memoryId": row.get::<_, String>("memory_id")?,
        "kind": row.get::<_, String>("kind")?,
        "key": row.get::<_, String>("key")?,
        "value": json!({}),
        "source": row.get::<_, String>("source")?,
        "createdAt": row.get::<_, String>("created_at")?,
        "metadata": parse_json(&metadata).unwrap_or(json!({})),
    });
    if let Some(artifact_id) = row.get::<_, Option<String>>("content_artifact_id")? {
        value["contentArtifactId"] = json!(artifact_id);
    }
    if let Some(sha) = row.get::<_, Option<String>>("content_sha256")? {
        value["contentSha256"] = json!(sha);
    }
    Ok(value)
}

fn map_session(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    Ok(json!({
        "sessionId": row.get::<_, String>("session_id")?,
        "startedAt": row.get::<_, String>("started_at")?,
        "endedAt": row.get::<_, Option<String>>("ended_at")?,
        "termination": row.get::<_, String>("termination")?,
        "episodeCount": row.get::<_, i64>("episode_count")?,
    }))
}

fn map_episode(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    Ok(json!({
        "episodeId": row.get::<_, String>("episode_id")?,
        "sessionId": row.get::<_, String>("session_id")?,
        "caseId": row.get::<_, Option<String>>("case_id")?,
        "signature": row.get::<_, String>("signature")?,
        "outcome": row.get::<_, String>("outcome")?,
        "startedAt": row.get::<_, String>("started_at")?,
        "completedAt": row.get::<_, Option<String>>("completed_at")?,
    }))
}

fn map_receipt(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let probabilities: String = row.get("probabilities_json")?;
    let thresholds: String = row.get("thresholds_json")?;
    let receipt_id: String = row.get("receipt_id")?;
    let selected_option_id: Option<String> = row
        .get::<_, Option<String>>("selected_option_id")?
        .or(row.get::<_, Option<String>>("selected_option")?);
    let mut value = json!({
        "receiptId": receipt_id.clone(),
        "decisionId": row.get::<_, Option<String>>("decision_id")?.unwrap_or(receipt_id),
        "caseId": row.get::<_, Option<String>>("case_id")?,
        "judgmentId": row.get::<_, Option<String>>("judgment_id")?,
        "reflexId": row.get::<_, Option<String>>("reflex_id")?,
        "gateId": row.get::<_, String>("gate_id")?,
        "policyVersion": row.get::<_, String>("policy_version")?,
        "questionType": row.get::<_, String>("question_type")?,
        "provider": row.get::<_, String>("provider")?,
        "probabilities": parse_json(&probabilities).unwrap_or(json!({})),
        "thresholds": parse_json(&thresholds).unwrap_or(json!({})),
        "optionLabels": json!({}),
        "selectedOption": selected_option_id.clone(),
        "selectedOptionId": selected_option_id,
        "result": row.get::<_, String>("result")?,
        "reasonCode": row.get::<_, String>("reason_code")?,
        "latencyMs": row.get::<_, Option<i64>>("latency_ms")?,
        "retries": row.get::<_, i64>("retries")?,
        "requestedAt": row.get::<_, Option<String>>("requested_at")?,
        "completedAt": row.get::<_, Option<String>>("completed_at")?,
        "createdAt": row.get::<_, String>("created_at")?,
    });
    if let Some(artifact_id) = row.get::<_, Option<String>>("labels_artifact_id")? {
        value["labelsArtifactId"] = json!(artifact_id);
    }
    if let Some(sha) = row.get::<_, Option<String>>("labels_sha256")? {
        value["labelsSha256"] = json!(sha);
    }
    Ok(value)
}

fn map_pattern(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let sessions: String = row.get("session_ids_json")?;
    let outcomes: String = row.get("outcomes_json")?;
    let evidence: String = row.get("evidence_ids_json")?;
    Ok(json!({
        "signature": row.get::<_, String>("signature")?,
        "count": row.get::<_, i64>("count")?,
        "sessionIds": parse_json(&sessions).unwrap_or(json!([])),
        "outcomes": parse_json(&outcomes).unwrap_or(json!({})),
        "firstAt": row.get::<_, String>("first_at")?,
        "lastAt": row.get::<_, String>("last_at")?,
        "evidenceIds": parse_json(&evidence).unwrap_or(json!([])),
    }))
}

fn map_candidate(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let mut value = json!({
        "candidateId": row.get::<_, String>("candidate_id")?,
        "signature": row.get::<_, String>("signature")?,
        "state": row.get::<_, String>("state")?,
        "because": "",
        "needed": row.get::<_, String>("needed")?,
        "updatedAt": row.get::<_, String>("updated_at")?,
    });
    if let Some(artifact_id) = row.get::<_, Option<String>>("because_artifact_id")? {
        value["becauseArtifactId"] = json!(artifact_id);
    }
    if let Some(sha) = row.get::<_, Option<String>>("because_sha256")? {
        value["becauseSha256"] = json!(sha);
    }
    if let Ok(Some(meta_raw)) = row.get::<_, Option<String>>("meta_json") {
        if let Ok(meta) = serde_json::from_str::<Value>(&meta_raw) {
            value["meta"] = meta;
        }
    }
    Ok(value)
}

fn map_pattern_evidence(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    Ok(json!({
        "evidenceId": row.get::<_, String>("evidence_id")?,
        "signature": row.get::<_, String>("signature")?,
        "sourceClass": row.get::<_, String>("source_class")?,
        "routeOrTool": row.get::<_, Option<String>>("route_or_tool")?,
        "userAction": row.get::<_, String>("user_action")?,
        "outcomeClass": row.get::<_, String>("outcome_class")?,
        "duplicateCount": row.get::<_, i64>("duplicate_count")?,
        "timeToActionMs": row.get::<_, Option<i64>>("time_to_action_ms")?,
        "feedback": row.get::<_, Option<String>>("feedback")?,
        "caseId": row.get::<_, Option<String>>("case_id")?,
        "createdAt": row.get::<_, String>("created_at")?,
    }))
}

fn map_review(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let mut value = json!({
        "reviewId": row.get::<_, String>("review_id")?,
        "triggerCode": row.get::<_, String>("trigger_code")?,
        "at": row.get::<_, String>("at")?,
        "findings": json!([]),
        "sessionsAtReview": row.get::<_, i64>("sessions_at_review")?,
        "episodesAtReview": row.get::<_, i64>("episodes_at_review")?,
        "candidatesAtReview": row.get::<_, i64>("candidates_at_review")?,
        "builtReflexesAtReview": row.get::<_, i64>("built_reflexes_at_review")?,
    });
    if let Some(artifact_id) = row.get::<_, Option<String>>("findings_artifact_id")? {
        value["findingsArtifactId"] = json!(artifact_id);
    }
    if let Some(sha) = row.get::<_, Option<String>>("findings_sha256")? {
        value["findingsSha256"] = json!(sha);
    }
    Ok(value)
}

fn map_judgment(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let mut value = json!({
        "judgmentId": row.get::<_, String>("judgment_id")?,
        "questionSetId": row.get::<_, String>("question_set_id")?,
        "questionSetVersion": row.get::<_, String>("question_set_version")?,
        "model": row.get::<_, String>("model")?,
        "status": row.get::<_, String>("status")?,
        "createdAt": row.get::<_, String>("created_at")?,
    });
    insert_opt(
        &mut value,
        "provider",
        row.get::<_, Option<String>>("provider")?,
    );
    insert_opt(
        &mut value,
        "caseId",
        row.get::<_, Option<String>>("case_id")?,
    );
    insert_opt_i64(
        &mut value,
        "caseVersion",
        row.get::<_, Option<i64>>("case_version")?,
    );
    insert_opt(
        &mut value,
        "requestArtifactId",
        row.get::<_, Option<String>>("request_artifact_id")?,
    );
    insert_opt(
        &mut value,
        "requestHash",
        row.get::<_, Option<String>>("request_hash")?,
    );
    insert_opt(
        &mut value,
        "responseArtifactId",
        row.get::<_, Option<String>>("response_artifact_id")?,
    );
    insert_opt(
        &mut value,
        "responseHash",
        row.get::<_, Option<String>>("response_hash")?,
    );
    insert_opt(
        &mut value,
        "failureCategory",
        row.get::<_, Option<String>>("failure_category")?,
    );
    insert_opt_i64(
        &mut value,
        "inputTokens",
        row.get::<_, Option<i64>>("input_tokens")?,
    );
    insert_opt_i64(
        &mut value,
        "outputTokens",
        row.get::<_, Option<i64>>("output_tokens")?,
    );
    insert_opt_i64(
        &mut value,
        "elapsedMs",
        row.get::<_, Option<i64>>("elapsed_ms")?,
    );
    insert_opt(
        &mut value,
        "completedAt",
        row.get::<_, Option<String>>("completed_at")?,
    );
    Ok(value)
}

fn insert_opt(value: &mut Value, key: &str, item: Option<String>) {
    if let Some(item) = item {
        value[key] = json!(item);
    }
}

fn insert_opt_i64(value: &mut Value, key: &str, item: Option<i64>) {
    if let Some(item) = item {
        value[key] = json!(item);
    }
}

fn query_values(
    conn: &Connection,
    sql: &str,
    params: impl rusqlite::Params,
    map: fn(&rusqlite::Row<'_>) -> rusqlite::Result<Value>,
) -> Result<Value, String> {
    let mut stmt = conn.prepare(sql).map_err(|error| error.to_string())?;
    let rows = stmt
        .query_map(params, map)
        .map_err(|error| error.to_string())?;
    let mut items = Vec::new();
    for row in rows {
        items.push(row.map_err(|error| error.to_string())?);
    }
    Ok(Value::Array(items))
}

fn req_str(value: &Value, key: &str) -> Result<String, String> {
    value
        .get(key)
        .and_then(|item| item.as_str())
        .map(str::to_string)
        .ok_or_else(|| format!("missing_{key}"))
}

fn req_i64(value: &Value, key: &str) -> Result<i64, String> {
    value
        .get(key)
        .and_then(|item| item.as_i64())
        .ok_or_else(|| format!("missing_{key}"))
}

fn req_obj<'a>(value: &'a Value, key: &str) -> Result<&'a Value, String> {
    value
        .get(key)
        .filter(|item| item.is_object())
        .ok_or_else(|| format!("missing_{key}"))
}

fn opt_str(value: &Value, key: &str) -> Option<String> {
    value
        .get(key)
        .and_then(|item| item.as_str())
        .map(str::to_string)
}

fn opt_i64(value: &Value, key: &str) -> Option<i64> {
    value.get(key).and_then(|item| item.as_i64())
}

fn json_text(value: &Value) -> Result<String, String> {
    serde_json::to_string(value).map_err(|error| error.to_string())
}

fn parse_json(text: &str) -> Result<Value, String> {
    serde_json::from_str(text).map_err(|error| error.to_string())
}

fn now_iso() -> String {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64;
    format_iso_ms(ms)
}

fn shift_iso(iso: &str, delta_ms: i64) -> Result<String, String> {
    Ok(format_iso_ms(parse_iso_ms(iso)? + delta_ms))
}

fn parse_iso_ms(iso: &str) -> Result<i64, String> {
    if iso.len() < 20 {
        return Err("bad_time".into());
    }
    let year: i64 = iso[0..4].parse().map_err(|_| "bad_time")?;
    let month: i64 = iso[5..7].parse().map_err(|_| "bad_time")?;
    let day: i64 = iso[8..10].parse().map_err(|_| "bad_time")?;
    let hour: i64 = iso[11..13].parse().map_err(|_| "bad_time")?;
    let minute: i64 = iso[14..16].parse().map_err(|_| "bad_time")?;
    let second: i64 = iso[17..19].parse().map_err(|_| "bad_time")?;
    let millis: i64 = if iso.len() >= 23 && iso.as_bytes().get(19) == Some(&b'.') {
        iso[20..23].parse().unwrap_or(0)
    } else {
        0
    };
    Ok(days_from_civil(year, month, day) * 86_400_000
        + hour * 3_600_000
        + minute * 60_000
        + second * 1000
        + millis)
}

fn format_iso_ms(ms: i64) -> String {
    let days = ms.div_euclid(86_400_000);
    let mut tod = ms.rem_euclid(86_400_000);
    let (year, month, day) = civil_from_days(days);
    let hour = tod / 3_600_000;
    tod %= 3_600_000;
    let minute = tod / 60_000;
    tod %= 60_000;
    let second = tod / 1000;
    let millis = tod % 1000;
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

fn days_from_civil(mut year: i64, month: i64, day: i64) -> i64 {
    year -= if month <= 2 { 1 } else { 0 };
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let yoe = (year - era * 400) as u64;
    let doy = (153 * (month + if month > 2 { -3 } else { 9 }) + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy as u64;
    era * 146097 + doe as i64 - 719468
}

fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let z = days + 719468;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = (z - era * 146097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let mut year = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = mp as i64 + if mp < 10 { 3 } else { -9 };
    if month <= 2 {
        year += 1;
    }
    (year, month, day as i64)
}

fn foundation_put(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "INSERT INTO foundation_records(kind, record_id, version, payload_json, updated_at)
         VALUES (?1, ?2, ?3, ?4, ?5)
         ON CONFLICT(kind, record_id) DO UPDATE SET
           version = excluded.version,
           payload_json = excluded.payload_json,
           updated_at = excluded.updated_at",
        params![
            req_str(op, "kind")?,
            req_str(op, "id")?,
            req_i64(op, "version")?,
            json_text(op.get("payload").unwrap_or(&Value::Null))?,
            req_str(op, "at")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn foundation_claim(conn: &Connection, op: &Value) -> Result<Value, String> {
    let changed = conn
        .execute(
            "INSERT INTO foundation_records(kind, record_id, version, payload_json, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5)
             ON CONFLICT(kind, record_id) DO NOTHING",
            params![
                req_str(op, "kind")?,
                req_str(op, "id")?,
                req_i64(op, "version")?,
                json_text(op.get("payload").unwrap_or(&Value::Null))?,
                req_str(op, "at")?,
            ],
        )
        .map_err(|error| error.to_string())?;
    Ok(Value::Bool(changed > 0))
}

fn foundation_get(conn: &Connection, op: &Value) -> Result<Value, String> {
    let row = conn
        .query_row(
            "SELECT record_id, version, payload_json, updated_at FROM foundation_records WHERE kind = ?1 AND record_id = ?2",
            params![req_str(op, "kind")?, req_str(op, "id")?],
            |row| {
                Ok(json!({
                    "id": row.get::<_, String>(0)?,
                    "version": row.get::<_, i64>(1)?,
                    "payload": parse_json(&row.get::<_, String>(2)?).unwrap_or(Value::Null),
                    "updatedAt": row.get::<_, String>(3)?,
                }))
            },
        )
        .optional()
        .map_err(|error| error.to_string())?;
    Ok(row.unwrap_or(Value::Null))
}

fn foundation_list(conn: &Connection, op: &Value) -> Result<Value, String> {
    let mut statement = conn
        .prepare("SELECT record_id, version, payload_json, updated_at FROM foundation_records WHERE kind = ?1")
        .map_err(|error| error.to_string())?;
    let rows = statement
        .query_map(params![req_str(op, "kind")?], |row| {
            Ok(json!({
                "id": row.get::<_, String>(0)?,
                "version": row.get::<_, i64>(1)?,
                "payload": parse_json(&row.get::<_, String>(2)?).unwrap_or(Value::Null),
                "updatedAt": row.get::<_, String>(3)?,
            }))
        })
        .map_err(|error| error.to_string())?;
    let mut items = Vec::new();
    for row in rows {
        items.push(row.map_err(|error| error.to_string())?);
    }
    Ok(Value::Array(items))
}

fn link_execution_case(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "INSERT OR IGNORE INTO execution_case_links(execution_id, project_case_id, linked_at) VALUES (?1, ?2, ?3)",
        params![req_str(op, "executionId")?, req_str(op, "projectCaseId")?, req_str(op, "at")?],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn list_execution_cases(conn: &Connection, op: &Value) -> Result<Value, String> {
    let mut statement = conn
        .prepare("SELECT project_case_id FROM execution_case_links WHERE execution_id = ?1")
        .map_err(|error| error.to_string())?;
    let rows = statement
        .query_map(params![req_str(op, "executionId")?], |row| {
            row.get::<_, String>(0)
        })
        .map_err(|error| error.to_string())?;
    let mut items = Vec::new();
    for row in rows {
        items.push(Value::String(row.map_err(|error| error.to_string())?));
    }
    Ok(Value::Array(items))
}

impl StateDb {
    fn case_folder_write(&self, op: &Value) -> Result<Value, String> {
        let case_id = req_str(op, "projectCaseId")?;
        let relative = req_str(op, "relativePath")?;
        let text = req_str(op, "text")?;
        let path = safe_case_path(&self.root, &case_id, &relative)?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }
        let tmp = path.with_extension("tmp");
        std::fs::write(&tmp, text.as_bytes()).map_err(|error| error.to_string())?;
        std::fs::rename(&tmp, &path).map_err(|error| error.to_string())?;
        Ok(Value::Null)
    }

    fn case_folder_read(&self, op: &Value) -> Result<Value, String> {
        let case_id = req_str(op, "projectCaseId")?;
        let relative = req_str(op, "relativePath")?;
        let path = safe_case_path(&self.root, &case_id, &relative)?;
        match std::fs::read_to_string(path) {
            Ok(text) => Ok(json!({ "text": text })),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(Value::Null),
            Err(error) => Err(error.to_string()),
        }
    }
}

fn safe_case_path(root: &Path, case_id: &str, relative: &str) -> Result<PathBuf, String> {
    if !matches!(
        relative,
        "main.md" | "rules.json" | "references.json" | "patterns.md"
    ) {
        return Err("unsafe_case_path".into());
    }
    if case_id.is_empty()
        || case_id.contains('/')
        || case_id.contains('\\')
        || case_id.contains("..")
    {
        return Err("unsafe_case_path".into());
    }
    Ok(root.join("cases").join(case_id).join(relative))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_db() -> (StateDb, PathBuf) {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!("relay-state-{nanos}.sqlite"));
        let db = StateDb::open(&path).expect("open");
        (db, path)
    }

    #[test]
    fn epoch_round_trips() {
        assert_eq!(format_iso_ms(0), "1970-01-01T00:00:00.000Z");
        assert_eq!(parse_iso_ms("1970-01-01T00:00:00.000Z").unwrap(), 0);
    }

    #[test]
    fn memory_survives_restart_and_key_is_unique() {
        let (mut db, path) = temp_db();
        let record = json!({
            "op": "put_memory",
            "record": {
                "memoryId": "mem_1",
                "kind": "glossary",
                "key": "MSRP",
                "value": {},
                "contentArtifactId": "artifact_first",
                "contentSha256": "aaa",
                "metadata": { "status": "confirmed" },
                "source": "explicit_user",
                "createdAt": "2020-01-01T00:00:00.000Z"
            }
        });
        db.execute(&record).unwrap();
        let replacement = json!({
            "op": "put_memory",
            "record": {
                "memoryId": "mem_2",
                "kind": "glossary",
                "key": "MSRP",
                "value": {},
                "contentArtifactId": "artifact_msrp",
                "contentSha256": "bbb",
                "metadata": { "status": "confirmed" },
                "source": "explicit_user",
                "createdAt": "2020-01-01T00:00:00.000Z"
            }
        });
        db.execute(&replacement).unwrap();
        drop(db);
        let mut reopened = StateDb::open(&path).unwrap();
        let rows = reopened.execute(&json!({ "op": "list_memories" })).unwrap();
        assert_eq!(rows.as_array().unwrap().len(), 1);
        let row = reopened
            .execute(&json!({ "op": "get_memory", "kind": "glossary", "key": "MSRP" }))
            .unwrap();
        assert_eq!(row["contentArtifactId"], "artifact_msrp");
        assert_eq!(row["contentSha256"], "bbb");
        assert_eq!(row["value"], json!({}));
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn stale_case_update_does_not_write() {
        let (mut db, path) = temp_db();
        db.execute(&json!({
            "op": "create_case",
            "caseId": "case_1",
            "origin": "direct",
            "kind": "resolve",
            "priority": 1,
            "at": "2020-01-01T00:00:00.000Z"
        }))
        .unwrap();
        let stale = db
            .execute(&json!({
                "op": "update_case",
                "caseId": "case_1",
                "expectedVersion": 0,
                "patch": { "status": "completed", "at": "2020-01-01T00:00:01.000Z" }
            }))
            .unwrap();
        assert!(stale.is_null());
        let live = db
            .execute(&json!({ "op": "get_case", "caseId": "case_1" }))
            .unwrap();
        assert_eq!(live["status"], "active");
        assert_eq!(live["version"], 1);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn claim_is_exclusive() {
        let (mut db, path) = temp_db();
        db.execute(&json!({
            "op": "enqueue",
            "item": {
                "workId": "work_1",
                "type": "source.final",
                "priority": 1,
                "availableAt": "2020-01-01T00:00:00.000Z",
                "payload": {},
                "createdAt": "2020-01-01T00:00:00.000Z"
            }
        }))
        .unwrap();
        let first = db
            .execute(&json!({
                "op": "claim_next",
                "now": "2020-01-01T00:00:00.000Z",
                "owner": "a",
                "leaseMs": 30000
            }))
            .unwrap();
        let second = db
            .execute(&json!({
                "op": "claim_next",
                "now": "2020-01-01T00:00:00.000Z",
                "owner": "b",
                "leaseMs": 30000
            }))
            .unwrap();
        assert_eq!(first["workId"], "work_1");
        assert!(second.is_null());
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn failed_op_rolls_back() {
        let (mut db, path) = temp_db();
        let error = db.execute(&json!({ "op": "put_memory", "record": { "kind": "glossary" } }));
        assert!(error.is_err());
        let rows = db.execute(&json!({ "op": "list_memories" })).unwrap();
        assert_eq!(rows.as_array().unwrap().len(), 0);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn explicit_transaction_rolls_back_multi_op() {
        let (mut db, path) = temp_db();
        db.execute(&json!({ "op": "begin_transaction" })).unwrap();
        db.execute(&json!({
            "op": "create_case",
            "caseId": "case_tx",
            "origin": "direct",
            "kind": "resolve",
            "priority": 1,
            "at": "2020-01-01T00:00:00.000Z"
        }))
        .unwrap();
        db.execute(&json!({
            "op": "append_domain_event",
            "type": "source.case_bound",
            "at": "2020-01-01T00:00:00.000Z",
            "payload": { "caseId": "case_tx" }
        }))
        .unwrap();
        db.execute(&json!({ "op": "rollback_transaction" }))
            .unwrap();
        let missing = db
            .execute(&json!({ "op": "get_case", "caseId": "case_tx" }))
            .unwrap();
        assert!(missing.is_null());
        let events = db
            .execute(&json!({ "op": "list_domain_events", "limit": 10 }))
            .unwrap();
        assert_eq!(events.as_array().unwrap().len(), 0);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn explicit_transaction_commits_multi_op() {
        let (mut db, path) = temp_db();
        db.execute(&json!({ "op": "begin_transaction" })).unwrap();
        db.execute(&json!({
            "op": "create_case",
            "caseId": "case_ok",
            "origin": "direct",
            "kind": "resolve",
            "priority": 1,
            "at": "2020-01-01T00:00:00.000Z"
        }))
        .unwrap();
        db.execute(&json!({
            "op": "append_domain_event",
            "type": "source.case_bound",
            "at": "2020-01-01T00:00:00.000Z",
            "payload": { "caseId": "case_ok" }
        }))
        .unwrap();
        db.execute(&json!({ "op": "commit_transaction" })).unwrap();
        let live = db
            .execute(&json!({ "op": "get_case", "caseId": "case_ok" }))
            .unwrap();
        assert_eq!(live["caseId"], "case_ok");
        let events = db
            .execute(&json!({ "op": "list_domain_events", "limit": 10 }))
            .unwrap();
        assert_eq!(events.as_array().unwrap().len(), 1);
        let _ = std::fs::remove_file(path);
    }
}
