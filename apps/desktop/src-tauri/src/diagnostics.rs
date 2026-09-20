use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const EVENTS_ROTATE_BYTES: u64 = 100_000_000;
const RUN_RETENTION: Duration = Duration::from_secs(30 * 24 * 60 * 60);

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

#[tauri::command]
pub fn trace_run_dir(run_id: String) -> Result<String, String> {
    let dir = run_dir(&run_id)?;
    fs::create_dir_all(&dir).map_err(|error| error.to_string())?;
    Ok(dir.to_string_lossy().to_string())
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
