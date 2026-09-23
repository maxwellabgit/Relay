use rusqlite::{params, Connection, OptionalExtension};
use serde_json::{json, Value};

pub fn dispatch(conn: &Connection, op: &str, value: &Value) -> Option<Result<Value, String>> {
    Some(match op {
        "save_hosted_grant" => save(conn, value),
        "revoke_hosted_grant" => revoke(conn, value),
        "find_hosted_grant" => find(conn, value),
        "read_hosted_grant" => read(conn, value),
        "reserve_hosted_grant" => reserve(conn, value),
        "commit_hosted_grant" => commit(conn, value),
        "release_hosted_grant" => release(conn, value),
        "release_uncommitted_hosted_grants" => release_uncommitted(conn),
        _ => return None,
    })
}

pub fn release_uncommitted_on_open(conn: &Connection) -> Result<(), String> {
    conn.execute(
        "UPDATE hosted_grant_reservations SET state = 'released' WHERE state = 'reserved'",
        [],
    )
    .map_err(|error| error.to_string())?;
    Ok(())
}

fn save(conn: &Connection, op: &Value) -> Result<Value, String> {
    let grant = op.get("grant").ok_or("missing_grant")?;
    let grant_id = req_str(grant, "grantId")?;
    let existing = conn
        .query_row(
            "SELECT requests_committed, bytes_committed, revoked_at FROM hosted_grants WHERE grant_id = ?1",
            params![grant_id],
            |row| {
                Ok((
                    row.get::<_, i64>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, Option<String>>(2)?,
                ))
            },
        )
        .optional()
        .map_err(|error| error.to_string())?;
    let (requests, bytes, revoked) = existing.unwrap_or((0, 0, None));
    let revoked_at = revoked.or_else(|| {
        grant
            .get("revokedAt")
            .and_then(|value| value.as_str())
            .map(str::to_string)
    });
    conn.execute(
        "INSERT INTO hosted_grants(grant_id, scope_kind, scope_id, created_at, expires_at, allowed_json, max_requests, max_bytes, revoked_at, requests_committed, bytes_committed)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
         ON CONFLICT(grant_id) DO UPDATE SET
           scope_kind = excluded.scope_kind,
           scope_id = excluded.scope_id,
           expires_at = excluded.expires_at,
           allowed_json = excluded.allowed_json,
           max_requests = excluded.max_requests,
           max_bytes = excluded.max_bytes,
           revoked_at = COALESCE(hosted_grants.revoked_at, excluded.revoked_at)",
        params![
            grant_id,
            req_str(grant, "scopeKind")?,
            req_str(grant, "scopeId")?,
            req_str(grant, "createdAt")?,
            req_str(grant, "expiresAt")?,
            grant.get("allowedSourceClasses").unwrap_or(&Value::Null).to_string(),
            req_i64(grant, "maxRequests")?,
            req_i64(grant, "maxBytes")?,
            revoked_at,
            requests,
            bytes,
        ],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn revoke(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "UPDATE hosted_grants SET revoked_at = ?1 WHERE grant_id = ?2",
        params![req_str(op, "at")?, req_str(op, "grantId")?],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn find(conn: &Connection, op: &Value) -> Result<Value, String> {
    let row = load(conn, &req_str(op, "grantId")?)?;
    Ok(row.map(map_grant).unwrap_or(Value::Null))
}

fn read(conn: &Connection, op: &Value) -> Result<Value, String> {
    let scope = op.get("scope").ok_or("missing_scope")?;
    let row = conn
        .query_row(
            "SELECT grant_id, scope_kind, scope_id, created_at, expires_at, allowed_json, max_requests, max_bytes, revoked_at, requests_committed, bytes_committed
             FROM hosted_grants WHERE scope_kind = ?1 AND scope_id = ?2 ORDER BY created_at DESC LIMIT 1",
            params![req_str(scope, "kind")?, req_str(scope, "id")?],
            read_row,
        )
        .optional()
        .map_err(|error| error.to_string())?;
    let Some(row) = row else {
        return Ok(json!({ "grant": null, "requestsUsed": 0, "bytesUsed": 0 }));
    };
    let held = conn
        .query_row(
            "SELECT COUNT(*), COALESCE(SUM(bytes), 0) FROM hosted_grant_reservations WHERE grant_id = ?1 AND state = 'reserved'",
            params![row.grant_id],
            |cursor| Ok((cursor.get::<_, i64>(0)?, cursor.get::<_, i64>(1)?)),
        )
        .map_err(|error| error.to_string())?;
    Ok(json!({
        "grant": map_grant(row.clone()),
        "requestsUsed": row.requests_committed + held.0,
        "bytesUsed": row.bytes_committed + held.1,
    }))
}

fn reserve(conn: &Connection, op: &Value) -> Result<Value, String> {
    let grant_id = req_str(op, "grantId")?;
    let Some(row) = load(conn, &grant_id)? else {
        return Ok(json!({ "ok": false, "reason": "missing" }));
    };
    if row.revoked_at.is_some() {
        return Ok(json!({ "ok": false, "reason": "revoked" }));
    }
    let now = req_str(op, "now")?;
    if chrono_after(&now, &row.expires_at) {
        return Ok(json!({ "ok": false, "reason": "expired" }));
    }
    let bytes = req_i64(op, "bytes")?;
    let held = conn
        .query_row(
            "SELECT COUNT(*), COALESCE(SUM(bytes), 0) FROM hosted_grant_reservations WHERE grant_id = ?1 AND state = 'reserved'",
            params![grant_id],
            |cursor| Ok((cursor.get::<_, i64>(0)?, cursor.get::<_, i64>(1)?)),
        )
        .map_err(|error| error.to_string())?;
    if row.requests_committed + held.0 + 1 > row.max_requests
        || row.bytes_committed + held.1 + bytes > row.max_bytes
    {
        return Ok(json!({ "ok": false, "reason": "exhausted" }));
    }
    let reservation_id = req_str(op, "reservationId")?;
    conn.execute(
        "INSERT INTO hosted_grant_reservations(reservation_id, grant_id, bytes, state, created_at) VALUES (?1, ?2, ?3, 'reserved', ?4)",
        params![reservation_id, grant_id, bytes, now],
    )
    .map_err(|error| error.to_string())?;
    Ok(json!({ "ok": true, "reservationId": reservation_id }))
}

fn commit(conn: &Connection, op: &Value) -> Result<Value, String> {
    let reservation_id = req_str(op, "reservationId")?;
    let hold = conn
        .query_row(
            "SELECT grant_id, bytes, state FROM hosted_grant_reservations WHERE reservation_id = ?1",
            params![reservation_id],
            |row| Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?, row.get::<_, String>(2)?)),
        )
        .optional()
        .map_err(|error| error.to_string())?;
    if let Some((grant_id, bytes, state)) = hold {
        if state == "reserved" {
            conn.execute(
                "UPDATE hosted_grants SET requests_committed = requests_committed + 1, bytes_committed = bytes_committed + ?1 WHERE grant_id = ?2",
                params![bytes, grant_id],
            )
            .map_err(|error| error.to_string())?;
            conn.execute(
                "UPDATE hosted_grant_reservations SET state = 'committed' WHERE reservation_id = ?1",
                params![reservation_id],
            )
            .map_err(|error| error.to_string())?;
        }
    }
    Ok(Value::Null)
}

fn release(conn: &Connection, op: &Value) -> Result<Value, String> {
    conn.execute(
        "UPDATE hosted_grant_reservations SET state = 'released' WHERE reservation_id = ?1 AND state = 'reserved'",
        params![req_str(op, "reservationId")?],
    )
    .map_err(|error| error.to_string())?;
    Ok(Value::Null)
}

fn release_uncommitted(conn: &Connection) -> Result<Value, String> {
    let changes = conn
        .execute(
            "UPDATE hosted_grant_reservations SET state = 'released' WHERE state = 'reserved'",
            [],
        )
        .map_err(|error| error.to_string())?;
    Ok(json!(changes))
}

#[derive(Clone)]
struct GrantRow {
    grant_id: String,
    scope_kind: String,
    scope_id: String,
    created_at: String,
    expires_at: String,
    allowed_json: String,
    max_requests: i64,
    max_bytes: i64,
    revoked_at: Option<String>,
    requests_committed: i64,
    bytes_committed: i64,
}

fn load(conn: &Connection, grant_id: &str) -> Result<Option<GrantRow>, String> {
    conn.query_row(
        "SELECT grant_id, scope_kind, scope_id, created_at, expires_at, allowed_json, max_requests, max_bytes, revoked_at, requests_committed, bytes_committed FROM hosted_grants WHERE grant_id = ?1",
        params![grant_id],
        read_row,
    )
    .optional()
    .map_err(|error| error.to_string())
}

fn read_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<GrantRow> {
    Ok(GrantRow {
        grant_id: row.get(0)?,
        scope_kind: row.get(1)?,
        scope_id: row.get(2)?,
        created_at: row.get(3)?,
        expires_at: row.get(4)?,
        allowed_json: row.get(5)?,
        max_requests: row.get(6)?,
        max_bytes: row.get(7)?,
        revoked_at: row.get(8)?,
        requests_committed: row.get(9)?,
        bytes_committed: row.get(10)?,
    })
}

fn map_grant(row: GrantRow) -> Value {
    let mut value = json!({
        "grantId": row.grant_id,
        "scopeKind": row.scope_kind,
        "scopeId": row.scope_id,
        "createdAt": row.created_at,
        "expiresAt": row.expires_at,
        "allowedSourceClasses": serde_json::from_str::<Value>(&row.allowed_json).unwrap_or(Value::Null),
        "maxRequests": row.max_requests,
        "maxBytes": row.max_bytes,
    });
    if let Some(revoked) = row.revoked_at {
        value["revokedAt"] = Value::String(revoked);
    }
    value
}

fn chrono_after(now: &str, expires: &str) -> bool {
    now >= expires
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
        .and_then(|item| {
            item.as_i64()
                .or_else(|| item.as_f64().map(|number| number as i64))
        })
        .ok_or_else(|| format!("missing_{key}"))
}
