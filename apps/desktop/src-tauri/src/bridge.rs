use serde_json::{json, Value};
use std::fs::{self, OpenOptions};
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

const MAX_MESSAGE: usize = 262_144;

pub fn relay_bridge_dir() -> PathBuf {
    let base = std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(base).join("RELAY").join("bridge")
}

pub fn run_host() -> io::Result<()> {
    binary_stdio();
    let dir = relay_bridge_dir();
    fs::create_dir_all(&dir)?;
    let stdin = io::stdin();
    let mut input = stdin.lock();
    let mut output = io::stdout();
    loop {
        let mut len_buf = [0u8; 4];
        if input.read_exact(&mut len_buf).is_err() {
            break;
        }
        let len = u32::from_ne_bytes(len_buf) as usize;
        if len == 0 || len > MAX_MESSAGE {
            let _ = write_message(
                &mut output,
                &json!({ "ok": false, "error": "oversized_observation" }).to_string(),
            );
            break;
        }
        let mut body = vec![0u8; len];
        if input.read_exact(&mut body).is_err() {
            break;
        }
        let text = String::from_utf8_lossy(&body);
        let response = handle_message(&text);
        if write_message(&mut output, &response).is_err() {
            break;
        }
    }
    Ok(())
}

fn handle_message(text: &str) -> String {
    if text.len() > MAX_MESSAGE {
        return json!({ "ok": false, "error": "oversized_observation" }).to_string();
    }
    let parsed: Value = match serde_json::from_str(text) {
        Ok(value) => value,
        Err(_) => return json!({ "ok": false, "error": "malformed_observation" }).to_string(),
    };
    let Some(object) = parsed.as_object() else {
        return json!({ "ok": false, "error": "malformed_observation" }).to_string();
    };
    if object.keys().any(|key| {
        let lower = key.to_lowercase();
        lower.contains("password")
            || lower.contains("secret")
            || lower.contains("token")
            || lower == "tool"
            || lower == "command"
            || lower == "execute"
    }) {
        return json!({ "ok": false, "error": "sensitive_blocked" }).to_string();
    }
    if object.get("v") != Some(&Value::from(1)) {
        return json!({ "ok": false, "error": "malformed_observation" }).to_string();
    }
    if object.get("kind") == Some(&Value::String("ping".into())) {
        let _ = write_heartbeat();
        return json!({ "ok": true, "pong": true }).to_string();
    }
    if object.get("kind") != Some(&Value::String("observation".into())) {
        return json!({ "ok": false, "error": "malformed_observation" }).to_string();
    }
    let event_type = object
        .get("eventType")
        .and_then(|value| value.as_str())
        .unwrap_or("");
    if !matches!(
        event_type,
        "browser.navigate" | "browser.activate" | "page.semantic"
    ) {
        return json!({ "ok": false, "error": "malformed_observation" }).to_string();
    }
    if !accepting() {
        return json!({ "ok": false, "error": "observation_paused" }).to_string();
    }
    let lower = text.to_lowercase();
    if lower.contains("password=")
        || lower.contains("password:")
        || lower.contains("begin private key")
        || lower.contains("begin rsa private key")
    {
        return json!({ "ok": false, "error": "sensitive_blocked" }).to_string();
    }
    if let Err(error) = append_inbox(text) {
        return json!({ "ok": false, "error": error }).to_string();
    }
    let _ = write_heartbeat();
    json!({ "ok": true }).to_string()
}

pub fn set_accept(accept: bool) -> io::Result<()> {
    let dir = relay_bridge_dir();
    fs::create_dir_all(&dir)?;
    fs::write(
        dir.join("control.json"),
        json!({ "accept": accept }).to_string(),
    )
}

pub fn drain_inbox() -> Result<Vec<Value>, String> {
    let path = relay_bridge_dir().join("inbox.jsonl");
    let text = match fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error.to_string()),
    };
    let _ = fs::write(&path, "");
    let mut out = Vec::new();
    for line in text.lines() {
        if line.trim().is_empty() {
            continue;
        }
        match serde_json::from_str::<Value>(line) {
            Ok(value) => out.push(value),
            Err(_) => out.push(json!({ "ok": false, "error": "malformed_observation" })),
        }
    }
    Ok(out)
}

pub fn bridge_status() -> (bool, String) {
    let path = relay_bridge_dir().join("heartbeat.json");
    let Ok(text) = fs::read_to_string(path) else {
        return (
            false,
            "Chrome is not connected. Install the RELAY extension and native host.".into(),
        );
    };
    let Ok(value) = serde_json::from_str::<Value>(&text) else {
        return (false, "Chrome bridge status is unreadable.".into());
    };
    let at = value
        .get("atMs")
        .and_then(|item| item.as_u64())
        .unwrap_or(0);
    let age = now_ms().saturating_sub(at);
    if age > 20_000 {
        return (
            false,
            "Chrome was connected, then the extension stopped sending.".into(),
        );
    }
    (true, "Chrome is connected.".into())
}

fn accepting() -> bool {
    let Ok(text) = fs::read_to_string(relay_bridge_dir().join("control.json")) else {
        return false;
    };
    serde_json::from_str::<Value>(&text)
        .ok()
        .and_then(|value| value.get("accept").and_then(|item| item.as_bool()))
        .unwrap_or(false)
}

fn append_inbox(text: &str) -> Result<(), String> {
    let dir = relay_bridge_dir();
    fs::create_dir_all(&dir).map_err(|error| error.to_string())?;
    let path = dir.join("inbox.jsonl");
    if fs::metadata(&path).map(|meta| meta.len()).unwrap_or(0) > 8_000_000 {
        return Err("oversized_observation".into());
    }
    let mut file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .map_err(|error| error.to_string())?;
    writeln!(file, "{text}").map_err(|error| error.to_string())
}

fn write_heartbeat() -> io::Result<()> {
    let dir = relay_bridge_dir();
    fs::create_dir_all(&dir)?;
    fs::write(
        dir.join("heartbeat.json"),
        json!({ "atMs": now_ms() }).to_string(),
    )
}

fn write_message(output: &mut impl Write, text: &str) -> io::Result<()> {
    let bytes = text.as_bytes();
    output.write_all(&(bytes.len() as u32).to_ne_bytes())?;
    output.write_all(bytes)?;
    output.flush()
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

fn binary_stdio() {
    #[cfg(windows)]
    unsafe {
        extern "C" {
            fn _setmode(fd: i32, mode: i32) -> i32;
        }
        const O_BINARY: i32 = 0x8000;
        _setmode(0, O_BINARY);
        _setmode(1, O_BINARY);
    }
}

pub fn read_allowed_text(path: &str) -> Result<String, String> {
    let file = std::path::Path::new(path);
    if !file.is_absolute() {
        return Err("malformed_observation".into());
    }
    let allowed = allowed_roots()?;
    let canonical = file
        .canonicalize()
        .map_err(|_| "master_unreadable".to_string())?;
    if !allowed.iter().any(|root| canonical.starts_with(root)) {
        return Err("sensitive_blocked".into());
    }
    let ext = canonical
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    match ext.as_str() {
        "txt" | "md" => fs::read_to_string(&canonical).map_err(|_| "master_unreadable".into()),
        "docx" => read_docx(&canonical),
        _ => Err("malformed_observation".into()),
    }
}

pub fn write_draft(name: &str, body: &str) -> Result<String, String> {
    if !name.ends_with(".md") || name.contains("..") || name.contains('/') || name.contains('\\') {
        return Err("malformed_observation".into());
    }
    let dir = PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into()))
        .join("RELAY")
        .join("drafts");
    fs::create_dir_all(&dir).map_err(|error| error.to_string())?;
    let path = dir.join(name);
    if path.extension().and_then(|value| value.to_str()) != Some("md") {
        return Err("malformed_observation".into());
    }
    fs::write(&path, body).map_err(|error| error.to_string())?;
    Ok(path.display().to_string())
}

pub fn remember_roots(folders: &[String], master: Option<&str>) -> io::Result<()> {
    let dir = relay_bridge_dir();
    fs::create_dir_all(&dir)?;
    let mut current = allowed_list();
    for folder in folders {
        if !current.iter().any(|item| item == folder) {
            current.push(folder.clone());
        }
    }
    if let Some(master) = master {
        if let Some(parent) = std::path::Path::new(master).parent() {
            let text = parent.display().to_string();
            if !current.iter().any(|item| item == &text) {
                current.push(text);
            }
        }
    }
    fs::write(
        dir.join("allow.json"),
        serde_json::to_string(&json!({ "folders": current }))?,
    )
}

fn allowed_list() -> Vec<String> {
    let Ok(text) = fs::read_to_string(relay_bridge_dir().join("allow.json")) else {
        return Vec::new();
    };
    serde_json::from_str::<Value>(&text)
        .ok()
        .and_then(|value| {
            value
                .get("folders")
                .and_then(|item| item.as_array())
                .cloned()
        })
        .unwrap_or_default()
        .into_iter()
        .filter_map(|item| item.as_str().map(|value| value.to_string()))
        .collect()
}

fn allowed_roots() -> Result<Vec<PathBuf>, String> {
    let mut roots = Vec::new();
    for folder in allowed_list() {
        let path = std::path::PathBuf::from(folder);
        if let Ok(canonical) = path.canonicalize() {
            roots.push(canonical);
        }
    }
    if roots.is_empty() {
        return Err("sensitive_blocked".into());
    }
    Ok(roots)
}

fn read_docx(path: &std::path::Path) -> Result<String, String> {
    let file = fs::File::open(path).map_err(|_| "master_unreadable".to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|_| "master_unreadable".to_string())?;
    let mut document = archive
        .by_name("word/document.xml")
        .map_err(|_| "master_unreadable".to_string())?;
    let mut xml = String::new();
    document
        .read_to_string(&mut xml)
        .map_err(|_| "master_unreadable".to_string())?;
    let mut out = String::new();
    let mut in_tag = false;
    for ch in xml.chars() {
        if ch == '<' {
            in_tag = true;
            continue;
        }
        if ch == '>' {
            in_tag = false;
            continue;
        }
        if !in_tag {
            out.push(ch);
        }
    }
    Ok(out.split_whitespace().collect::<Vec<_>>().join(" "))
}
