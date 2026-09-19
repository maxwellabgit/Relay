use std::fs;
use std::path::PathBuf;

fn runs_root() -> Result<PathBuf, String> {
    let base = std::env::var("LOCALAPPDATA").map_err(|_| "localappdata_missing".to_string())?;
    let root = PathBuf::from(base).join("RELAY").join("runs");
    fs::create_dir_all(&root).map_err(|error| error.to_string())?;
    Ok(root)
}

fn run_dir(run_id: &str) -> Result<PathBuf, String> {
    if !run_id.starts_with("run_") || !run_id.chars().all(|ch| ch.is_ascii_alphanumeric() || ch == '_') {
        return Err("rejected".to_string());
    }
    Ok(runs_root()?.join(run_id))
}

fn allow_line(line: &str) -> Result<String, String> {
    if line.len() > 4000 || line.contains('\n') || line.contains('\r') {
        return Err("rejected".to_string());
    }
    let value: serde_json::Value = serde_json::from_str(line).map_err(|_| "rejected".to_string())?;
    let object = value.as_object().ok_or_else(|| "rejected".to_string())?;
    let allowed = [
        "schemaVersion",
        "sequence",
        "at",
        "type",
        "caseId",
        "segmentId",
        "judgmentId",
        "reflexId",
        "reflexVersion",
        "probabilities",
        "thresholds",
        "selectedOutcome",
        "reasonCode",
        "latencyMs",
        "artifactRef",
        "queueDepth",
        "waitState",
    ];
    if object.keys().any(|key| !allowed.contains(&key.as_str())) {
        return Err("rejected".to_string());
    }
    if object.get("schemaVersion") != Some(&serde_json::Value::from(1)) {
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
    let mut file = fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
        .map_err(|error| error.to_string())?;
    use std::io::Write;
    writeln!(file, "{kept}").map_err(|error| error.to_string())?;
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
