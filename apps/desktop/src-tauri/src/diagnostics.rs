use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const EVENTS_ROTATE_BYTES: u64 = 100_000_000;
const RUN_RETENTION: Duration = Duration::from_secs(30 * 24 * 60 * 60);

static ACTIVE_RUN_ID: Mutex<Option<String>> = Mutex::new(None);

fn diagnostics_root() -> Result<PathBuf, String> {
    if let Ok(override_root) = std::env::var("RELAY_DIAGNOSTICS_ROOT") {
        let trimmed = override_root.trim();
        if !trimmed.is_empty() {
            let root = PathBuf::from(trimmed);
            fs::create_dir_all(&root).map_err(|error| error.to_string())?;
            return Ok(root);
        }
    }
    let base = std::env::var("LOCALAPPDATA").map_err(|_| "localappdata_missing".to_string())?;
    let root = PathBuf::from(base).join("RELAY").join("diagnostics");
    fs::create_dir_all(&root).map_err(|error| error.to_string())?;
    Ok(root)
}

fn runs_root() -> Result<PathBuf, String> {
    let root = diagnostics_root()?.join("runs");
    fs::create_dir_all(&root).map_err(|error| error.to_string())?;
    Ok(root)
}

fn run_dir(run_id: &str) -> Result<PathBuf, String> {
    if !run_id.starts_with("run_")
        || run_id.len() > 48
        || !run_id
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '_' || ch == '-')
    {
        return Err("rejected".to_string());
    }
    Ok(runs_root()?.join(run_id))
}

fn allow_line(line: &str) -> Result<String, String> {
    if line.len() > 4000 || line.contains('\n') || line.contains('\r') {
        return Err("rejected".to_string());
    }
    let value: serde_json::Value =
        serde_json::from_str(line).map_err(|_| "rejected".to_string())?;
    let object = value.as_object().ok_or_else(|| "rejected".to_string())?;
    let allowed = [
        "schemaVersion",
        "sequence",
        "runId",
        "at",
        "eventType",
        "stage",
        "status",
        "sessionId",
        "episodeId",
        "caseId",
        "workId",
        "judgmentId",
        "decisionId",
        "toolCallId",
        "operationId",
        "receiptId",
        "reflexId",
        "sourceEventId",
        "turnId",
        "parentEventId",
        "causedBy",
        "decisionOwner",
        "reasonCode",
        "durationMs",
        "attempt",
        "queueDepth",
    ];
    if object.keys().any(|key| !allowed.contains(&key.as_str())) {
        return Err("rejected".to_string());
    }
    if object.get("schemaVersion") != Some(&serde_json::Value::from(2)) {
        return Err("rejected".to_string());
    }
    let mut kept = serde_json::Map::new();
    for key in allowed {
        if let Some(item) = object.get(key) {
            kept.insert(key.to_string(), item.clone());
        }
    }
    serde_json::to_string(&kept).map_err(|error| error.to_string())
}

fn rotate_events_if_needed(path: &Path) -> Result<(), String> {
    let meta = match fs::metadata(path) {
        Ok(meta) => meta,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.to_string()),
    };
    if meta.len() <= EVENTS_ROTATE_BYTES {
        return Ok(());
    }
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    let rotated = path.with_file_name(format!("events-{millis}.jsonl"));
    fs::rename(path, rotated).map_err(|error| error.to_string())
}

fn prune_old_runs() {
    let Ok(root) = runs_root() else {
        return;
    };
    let Ok(now) = SystemTime::now().duration_since(UNIX_EPOCH) else {
        return;
    };
    let cutoff = now.saturating_sub(RUN_RETENTION);
    let Ok(entries) = fs::read_dir(&root) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let Ok(meta) = entry.metadata() else {
            continue;
        };
        if !meta.is_dir() {
            continue;
        }
        let Ok(modified) = meta.modified() else {
            continue;
        };
        let Ok(modified_since) = modified.duration_since(UNIX_EPOCH) else {
            continue;
        };
        if modified_since < cutoff {
            let _ = fs::remove_dir_all(path);
        }
    }
}

fn utc_now_iso() -> String {
    let total = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let secs = total.as_secs() as i64;
    let millis = total.subsec_millis();
    let days = secs.div_euclid(86_400);
    let day_secs = secs.rem_euclid(86_400) as u32;
    let (year, month, day) = civil_from_days(days);
    let hour = day_secs / 3600;
    let minute = (day_secs % 3600) / 60;
    let second = day_secs % 60;
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

fn civil_from_days(days: i64) -> (i32, u32, u32) {
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u32;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = (yoe as i64) + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = if m <= 2 { y + 1 } else { y };
    (year as i32, m, d)
}

fn set_active_run(run_id: &str) {
    if let Ok(mut guard) = ACTIVE_RUN_ID.lock() {
        *guard = Some(run_id.to_string());
    }
}

fn write_atomic(path: &Path, body: &str) -> Result<(), String> {
    let parent = path.parent().ok_or_else(|| "path_invalid".to_string())?;
    fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    let tmp = parent.join(format!(
        ".{}.tmp",
        path.file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("tmp")
    ));
    fs::write(&tmp, body).map_err(|error| error.to_string())?;
    fs::rename(&tmp, path).map_err(|error| error.to_string())
}

fn mark_prior_unclean_runs() {
    let Ok(root) = runs_root() else {
        return;
    };
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    for entry in entries.flatten() {
        let manifest_path = entry.path().join("manifest.json");
        let Ok(text) = fs::read_to_string(&manifest_path) else {
            continue;
        };
        let Ok(mut value) = serde_json::from_str::<serde_json::Value>(text.trim()) else {
            continue;
        };
        let Some(object) = value.as_object_mut() else {
            continue;
        };
        let status = object
            .get("status")
            .and_then(|item| item.as_str())
            .unwrap_or("");
        if status != "running" {
            continue;
        }
        object.insert(
            "status".to_string(),
            serde_json::Value::String("unclean_shutdown".to_string()),
        );
        object.insert(
            "endedAt".to_string(),
            serde_json::Value::String(utc_now_iso()),
        );
        object.insert("uncleanShutdown".to_string(), serde_json::Value::Bool(true));
        let _ = write_atomic(&manifest_path, &format!("{}\n", value));
    }
}

fn resolve_started_at(run_id: &str, run_dir_path: &Path) -> String {
    if let Ok(root) = diagnostics_root() {
        let pointer = root.join("latest.json");
        if let Ok(text) = fs::read_to_string(&pointer) {
            if let Ok(value) = serde_json::from_str::<serde_json::Value>(text.trim()) {
                if value.get("runId").and_then(|item| item.as_str()) == Some(run_id) {
                    if let Some(started) = value.get("startedAt").and_then(|item| item.as_str()) {
                        if !started.is_empty() {
                            return started.to_string();
                        }
                    }
                }
            }
        }
    }
    let manifest = run_dir_path.join("manifest.json");
    if let Ok(text) = fs::read_to_string(&manifest) {
        if let Ok(value) = serde_json::from_str::<serde_json::Value>(text.trim()) {
            if let Some(started) = value.get("startedAt").and_then(|item| item.as_str()) {
                if !started.is_empty() {
                    return started.to_string();
                }
            }
        }
    }
    utc_now_iso()
}

fn write_latest_pointer(
    run_id: &str,
    run_dir_path: &Path,
    clean_shutdown: Option<bool>,
    unclean_shutdown: bool,
) -> Result<(), String> {
    let pointer = diagnostics_root()?.join("latest.json");
    let body = serde_json::json!({
        "schemaVersion": 1,
        "runId": run_id,
        "runDir": run_dir_path.to_string_lossy(),
        "commit": env!("GIT_COMMIT"),
        "profile": std::env::var("RELAY_PROFILE").unwrap_or_else(|_| "desktop".to_string()),
        "mode": "live",
        "startedAt": resolve_started_at(run_id, run_dir_path),
        "heartbeatAt": utc_now_iso(),
        "pid": std::process::id(),
        "cleanShutdown": clean_shutdown,
        "uncleanShutdown": unclean_shutdown
    });
    write_atomic(&pointer, &format!("{}\n", body))
}

fn write_heartbeat_status(
    run_id: &str,
    status: &str,
    clean_shutdown: Option<bool>,
    unclean_shutdown: bool,
) -> Result<(), String> {
    let dir = run_dir(run_id)?;
    let path = dir.join("heartbeat.json");
    let body = serde_json::json!({
        "schemaVersion": 1,
        "runId": run_id,
        "at": utc_now_iso(),
        "pid": std::process::id(),
        "status": status
    });
    write_atomic(&path, &format!("{}\n", body))?;
    write_latest_pointer(run_id, &dir, clean_shutdown, unclean_shutdown)
}

fn write_heartbeat(run_id: &str) -> Result<(), String> {
    write_heartbeat_status(run_id, "running", None, false)
}

fn write_live_summary_stub(run_id: &str) -> Result<(), String> {
    let dir = run_dir(run_id)?;
    let events_path = dir.join("events.jsonl");
    let summary_path = dir.join("live-summary.json");
    let pointer = diagnostics_root()?.join("latest.json");
    let body = serde_json::json!({
        "schemaVersion": 1,
        "runId": run_id,
        "commit": env!("GIT_COMMIT"),
        "profile": std::env::var("RELAY_PROFILE").unwrap_or_else(|_| "desktop".to_string()),
        "providerReadiness": {
            "jev": "not_observed",
            "model": "not_observed",
            "audio": "not_observed"
        },
        "queue": { "ready": 0, "oldestReadyMs": 0, "deadLetters": 0 },
        "activeCases": 0,
        "waitingCases": 0,
        "failedCases": 0,
        "latestCase": { "caseId": null, "stage": null, "blocker": null },
        "latestJudgment": {
            "questionSet": null,
            "selectedRoute": null,
            "policyResult": null,
            "observed": false
        },
        "latestTool": { "toolId": null, "result": null, "observed": false },
        "deadLetters": 0,
        "retrySchedule": null,
        "paths": {
            "runDir": dir.to_string_lossy(),
            "eventsPath": events_path.to_string_lossy(),
            "liveSummaryPath": summary_path.to_string_lossy(),
            "latestPointerPath": pointer.to_string_lossy()
        },
        "updatedAt": utc_now_iso()
    });
    write_atomic(&summary_path, &format!("{}\n", body))
}

fn complete_manifest(run_id: &str, clean: bool) -> Result<(), String> {
    let path = run_dir(run_id)?.join("manifest.json");
    if !path.exists() {
        return Ok(());
    }
    let text = fs::read_to_string(&path).map_err(|error| error.to_string())?;
    let mut value: serde_json::Value =
        serde_json::from_str(text.trim()).map_err(|error| error.to_string())?;
    let object = value
        .as_object_mut()
        .ok_or_else(|| "manifest_invalid".to_string())?;
    let status = object
        .get("status")
        .and_then(|item| item.as_str())
        .unwrap_or("running");
    if status != "running" {
        return Ok(());
    }
    object.insert(
        "status".to_string(),
        serde_json::Value::String(if clean {
            "completed".to_string()
        } else {
            "unclean_shutdown".to_string()
        }),
    );
    object.insert(
        "endedAt".to_string(),
        serde_json::Value::String(utc_now_iso()),
    );
    object.insert(
        "uncleanShutdown".to_string(),
        serde_json::Value::Bool(!clean),
    );
    write_atomic(&path, &format!("{}\n", value))?;
    write_heartbeat_status(
        run_id,
        if clean {
            "completed"
        } else {
            "unclean_shutdown"
        },
        Some(clean),
        !clean,
    )?;
    Ok(())
}

/// Mark the active run completed on graceful process exit.
pub fn complete_active_run() {
    let run_id = ACTIVE_RUN_ID.lock().ok().and_then(|guard| guard.clone());
    if let Some(run_id) = run_id {
        let _ = complete_manifest(&run_id, true);
    }
}

#[tauri::command]
pub fn trace_run_dir(run_id: String) -> Result<String, String> {
    mark_prior_unclean_runs();
    let dir = run_dir(&run_id)?;
    fs::create_dir_all(&dir).map_err(|error| error.to_string())?;
    set_active_run(&run_id);
    let manifest = dir.join("manifest.json");
    if !manifest.exists() {
        let body = serde_json::json!({
            "schemaVersion": 1,
            "runId": run_id,
            "applicationVersion": env!("CARGO_PKG_VERSION"),
            "gitCommit": env!("GIT_COMMIT"),
            "protocolVersion": "2",
            "reflexVersions": { "resolve-acronym": 1 },
            "policyVersions": { "resolve-acronym@1": "v1" },
            "providerModes": ["typesafe", "local_model"],
            "startedAt": utc_now_iso(),
            "status": "running",
            "uncleanShutdown": false
        });
        write_atomic(&manifest, &format!("{}\n", body))?;
    }
    write_heartbeat(&run_id)?;
    write_live_summary_stub(&run_id)?;
    Ok(dir.to_string_lossy().to_string())
}

#[tauri::command]
pub fn complete_trace_run(run_id: String) -> Result<String, String> {
    complete_manifest(&run_id, true)?;
    Ok(run_dir(&run_id)?.to_string_lossy().to_string())
}

#[tauri::command]
pub fn append_trace_event(run_id: String, line: String) -> Result<String, String> {
    let kept = allow_line(&line)?;
    let dir = run_dir(&run_id)?;
    fs::create_dir_all(&dir).map_err(|error| error.to_string())?;
    let path = dir.join("events.jsonl");
    rotate_events_if_needed(&path)?;
    let mut file = fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
        .map_err(|error| error.to_string())?;
    writeln!(file, "{kept}").map_err(|error| error.to_string())?;
    let _ = write_heartbeat(&run_id);
    prune_old_runs();
    Ok(path.to_string_lossy().to_string())
}

#[tauri::command]
pub fn write_live_summary(run_id: String, body: String) -> Result<String, String> {
    if body.len() > 50_000 || body.contains('\0') {
        return Err("rejected".to_string());
    }
    let parsed: serde_json::Value =
        serde_json::from_str(&body).map_err(|_| "rejected".to_string())?;
    if parsed.get("schemaVersion") != Some(&serde_json::Value::from(1)) {
        return Err("rejected".to_string());
    }
    let path = run_dir(&run_id)?.join("live-summary.json");
    write_atomic(&path, &format!("{}\n", parsed))?;
    let _ = write_heartbeat(&run_id);
    Ok(path.to_string_lossy().to_string())
}

#[tauri::command]
pub fn read_trace_events(run_id: String) -> Result<String, String> {
    let path = run_dir(&run_id)?.join("events.jsonl");
    match fs::read_to_string(path) {
        Ok(text) => Ok(text),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(String::new()),
        Err(error) => Err(error.to_string()),
    }
}

#[tauri::command]
pub fn open_run_folder() -> Result<String, String> {
    let root = diagnostics_root()?;
    std::process::Command::new("explorer")
        .arg(&root)
        .spawn()
        .map_err(|error| error.to_string())?;
    Ok(root.to_string_lossy().to_string())
}

#[tauri::command]
pub fn diagnostics_latest_path() -> Result<String, String> {
    Ok(diagnostics_root()?
        .join("latest.json")
        .to_string_lossy()
        .to_string())
}

#[cfg(test)]
mod tests {
    use super::{civil_from_days, utc_now_iso};

    #[test]
    fn civil_from_days_unix_epoch() {
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        assert_eq!(civil_from_days(1), (1970, 1, 2));
        assert_eq!(civil_from_days(19_000), (2022, 1, 8));
    }

    #[test]
    fn utc_now_iso_shape() {
        let value = utc_now_iso();
        assert!(value.ends_with('Z'));
        assert_eq!(value.len(), 24);
        assert_eq!(&value[4..5], "-");
        assert_eq!(&value[10..11], "T");
    }
}
