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
const RETENTION_MS: i64 = 7 * 24 * 60 * 60 * 1000;

pub struct StateDb {
    conn: Connection,
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
        let mut db = Self { conn };
        db.migrate()?;
        Ok(db)
    }

    pub fn execute(&mut self, op: &Value) -> Result<Value, String> {
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
        tx.commit().map_err(|error| error.to_string())?;
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
        "list_episodes" => list_episodes(conn),
        "put_receipt" => put_receipt(conn, op),
        "list_receipts" => list_receipts(conn),
        "put_pattern" => put_pattern(conn, op),
        "get_pattern" => get_pattern(conn, op),
        "list_patterns" => list_patterns(conn),
        "put_candidate" => put_candidate(conn, op),
        "list_candidates" => list_candidates(conn),
        "put_review" => put_review(conn, op),
        "list_reviews" => list_reviews(conn),
        "compact" => compact(conn, op),
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
            "SELECT payload_json FROM domain_events WHERE type = 'feed.item' ORDER BY sequence",
        )
        .map_err(|error| error.to_string())?;
    let rows = stmt
        .query_map([], |row| row.get::<_, String>(0))
        .map_err(|error| error.to_string())?;
    let mut items = Vec::new();
    for row in rows {
        items.push(parse_json(&row.map_err(|error| error.to_string())?)?);
    }
    Ok(Value::Array(items))
}

fn add_feed_item(conn: &Connection, op: &Value) -> Result<Value, String> {
    let item = req_obj(op, "item")?;
    append_domain_event_raw(conn, "feed.item", &req_str(item, "createdAt")?, item)?;
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
        "INSERT INTO work_items(work_id, type, priority, available_at, payload_json, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![
            req_str(item, "workId")?,
            req_str(item, "type")?,
            req_i64(item, "priority")?,
            req_str(item, "availableAt")?,
            json_text(item.get("payload").unwrap_or(&json!({})))?,
            req_str(item, "createdAt")?,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn claim_next(conn: &Connection, op: &Value) -> Result<Value, String> {
    let now = req_str(op, "now")?;
    let row = conn
        .query_row(
            "SELECT work_id, type, priority, available_at, payload_json, created_at FROM work_items
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
                ))
            },
        )
        .optional()
        .map_err(|error| error.to_string())?;
    let Some((work_id, kind, priority, available_at, payload, created_at)) = row else {
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
    Ok(json!({
        "workId": work_id,
        "type": kind,
        "priority": priority,
        "availableAt": available_at,
        "payload": parse_json(&payload)?,
        "createdAt": created_at,
    }))
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
    conn.execute(
        "INSERT INTO memories(memory_id, kind, key, value_json, source, created_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6)
         ON CONFLICT(kind, key) DO UPDATE SET value_json=excluded.value_json, source=excluded.source",
        params![
            req_str(record, "memoryId")?,
            req_str(record, "kind")?,
            req_str(record, "key")?,
            json_text(record.get("value").unwrap_or(&json!({})))?,
            req_str(record, "source")?,
            req_str(record, "createdAt")?,
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
        "INSERT INTO work_episodes(episode_id, session_id, case_id, signature, outcome, started_at, completed_at)
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

fn list_episodes(conn: &Connection) -> Result<Value, String> {
    query_values(conn, "SELECT * FROM work_episodes", params![], map_episode)
}

fn put_receipt(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT INTO decision_receipts(
          receipt_id, case_id, gate_id, policy_version, question_type, provider,
          probabilities_json, thresholds_json, selected_option, result, reason_code,
          latency_ms, retries, created_at
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)",
        params![
            req_str(record, "receiptId")?,
            opt_str(record, "caseId"),
            req_str(record, "gateId")?,
            req_str(record, "policyVersion")?,
            req_str(record, "questionType")?,
            req_str(record, "provider")?,
            json_text(record.get("probabilities").unwrap_or(&json!({})))?,
            json_text(record.get("thresholds").unwrap_or(&json!({})))?,
            opt_str(record, "selectedOption"),
            req_str(record, "result")?,
            req_str(record, "reasonCode")?,
            opt_i64(record, "latencyMs"),
            req_i64(record, "retries")?,
            req_str(record, "createdAt")?,
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
    conn.execute(
        "INSERT OR REPLACE INTO expansion_candidates(candidate_id, signature, state, because, needed, updated_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![
            req_str(record, "candidateId")?,
            req_str(record, "signature")?,
            req_str(record, "state")?,
            req_str(record, "because")?,
            req_str(record, "needed")?,
            req_str(record, "updatedAt")?,
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

fn put_review(conn: &Connection, op: &Value) -> Result<Value, String> {
    let record = req_obj(op, "record")?;
    conn.execute(
        "INSERT OR IGNORE INTO review_runs(
          review_id, trigger_code, at, findings_json,
          sessions_at_review, episodes_at_review, candidates_at_review, built_reflexes_at_review
        ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
        params![
            req_str(record, "reviewId")?,
            req_str(record, "triggerCode")?,
            req_str(record, "at")?,
            json_text(record.get("findings").unwrap_or(&json!([])))?,
            req_i64(record, "sessionsAtReview")?,
            req_i64(record, "episodesAtReview")?,
            req_i64(record, "candidatesAtReview")?,
            req_i64(record, "builtReflexesAtReview")?,
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
    let value_json: String = row.get("value_json")?;
    Ok(json!({
        "memoryId": row.get::<_, String>("memory_id")?,
        "kind": row.get::<_, String>("kind")?,
        "key": row.get::<_, String>("key")?,
        "value": parse_json(&value_json).unwrap_or(json!({})),
        "source": row.get::<_, String>("source")?,
        "createdAt": row.get::<_, String>("created_at")?,
    }))
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
    Ok(json!({
        "receiptId": row.get::<_, String>("receipt_id")?,
        "caseId": row.get::<_, Option<String>>("case_id")?,
        "gateId": row.get::<_, String>("gate_id")?,
        "policyVersion": row.get::<_, String>("policy_version")?,
        "questionType": row.get::<_, String>("question_type")?,
        "provider": row.get::<_, String>("provider")?,
        "probabilities": parse_json(&probabilities).unwrap_or(json!({})),
        "thresholds": parse_json(&thresholds).unwrap_or(json!({})),
        "selectedOption": row.get::<_, Option<String>>("selected_option")?,
        "result": row.get::<_, String>("result")?,
        "reasonCode": row.get::<_, String>("reason_code")?,
        "latencyMs": row.get::<_, Option<i64>>("latency_ms")?,
        "retries": row.get::<_, i64>("retries")?,
        "createdAt": row.get::<_, String>("created_at")?,
    }))
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
    Ok(json!({
        "candidateId": row.get::<_, String>("candidate_id")?,
        "signature": row.get::<_, String>("signature")?,
        "state": row.get::<_, String>("state")?,
        "because": row.get::<_, String>("because")?,
        "needed": row.get::<_, String>("needed")?,
        "updatedAt": row.get::<_, String>("updated_at")?,
    }))
}

fn map_review(row: &rusqlite::Row<'_>) -> rusqlite::Result<Value> {
    let findings: String = row.get("findings_json")?;
    Ok(json!({
        "reviewId": row.get::<_, String>("review_id")?,
        "triggerCode": row.get::<_, String>("trigger_code")?,
        "at": row.get::<_, String>("at")?,
        "findings": parse_json(&findings).unwrap_or(json!([])),
        "sessionsAtReview": row.get::<_, i64>("sessions_at_review")?,
        "episodesAtReview": row.get::<_, i64>("episodes_at_review")?,
        "candidatesAtReview": row.get::<_, i64>("candidates_at_review")?,
        "builtReflexesAtReview": row.get::<_, i64>("built_reflexes_at_review")?,
    }))
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
                "value": { "expansion": "First" },
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
                "value": { "expansion": "Manufacturer Suggested Retail Price" },
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
        assert_eq!(
            row["value"]["expansion"],
            "Manufacturer Suggested Retail Price"
        );
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
}
