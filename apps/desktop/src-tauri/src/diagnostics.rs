use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const EVENTS_ROTATE_BYTES: u64 = 100_000_000;
const RUN_RETENTION: Duration = Duration::from_secs(30 * 24 * 60 * 60);

static ACTIVE_RUN_ID: Mutex<Option<String>> = Mutex::new(None);

fn runs_root() -> Result<PathBuf, String> {
    let base = std::env::var("LOCALAPPDATA").map_err(|_| "localappdata_missing".to_string())?;
    let root = PathBuf::from(base).join("RELAY").join("runs");
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
        "receiptId",
        "reflexId",
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

/// Howard Hinnant's civil_from_days — days since Unix epoch → Y-M-D.
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

fn complete_manifest(run_id: &str) -> Result<(), String> {
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
        serde_json::Value::String("completed".to_string()),
    );
    object.insert(
        "endedAt".to_string(),
        serde_json::Value::String(utc_now_iso()),
    );
    fs::write(&path, format!("{}\n", value)).map_err(|error| error.to_string())?;
    Ok(())
}

/// Mark the active run completed on graceful process exit.
/// Leaves unclean `running` manifests untouched when a new run starts.
pub fn complete_active_run() {
    let run_id = ACTIVE_RUN_ID.lock().ok().and_then(|guard| guard.clone());
    if let Some(run_id) = run_id {
        let _ = complete_manifest(&run_id);
    }
}

#[tauri::command]
pub fn trace_run_dir(run_id: String) -> Result<String, String> {
    let dir = run_dir(&run_id)?;
    fs::create_dir_all(&dir).map_err(|error| error.to_string())?;
    set_active_run(&run_id);
    let manifest = dir.join("manifest.json");
    if !manifest.exists() {
        // Prior unclean `running` manifests in other run dirs are left as-is.
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
            "status": "running"
        });
        fs::write(&manifest, format!("{}\n", body)).map_err(|error| error.to_string())?;
    }
    Ok(dir.to_string_lossy().to_string())
}

#[tauri::command]
pub fn complete_trace_run(run_id: String) -> Result<String, String> {
    complete_manifest(&run_id)?;
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
    use std::io::Write;
    writeln!(file, "{kept}").map_err(|error| error.to_string())?;
    prune_old_runs();
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
    let root = runs_root()?;
    std::process::Command::new("explorer")
        .arg(&root)
        .spawn()
        .map_err(|error| error.to_string())?;
    Ok(root.to_string_lossy().to_string())
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
