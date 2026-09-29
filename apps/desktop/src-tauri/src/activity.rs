#[cfg(windows)]
use relay_activity_bridge::now_iso;
use relay_activity_bridge::{validate_client_json, ClientMessage};
use serde_json::{json, Value};
use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};
use std::thread;
use std::time::Duration;
use tauri::{AppHandle, Emitter};

const BRIDGE_NAME: &str = "chrome-bridge.json";

struct ActivityHost {
    running: bool,
    stop: Option<std::sync::Arc<AtomicBool>>,
    policy: Value,
    port: u16,
    token: String,
}

fn host() -> &'static Mutex<ActivityHost> {
    static HOST: OnceLock<Mutex<ActivityHost>> = OnceLock::new();
    HOST.get_or_init(|| {
        Mutex::new(ActivityHost {
            running: false,
            stop: None,
            policy: json!({
                "enabled": false,
                "windowsEnabled": true,
                "chromeEnabled": true,
                "pageContentEnabled": false,
                "permittedDomains": []
            }),
            port: 0,
            token: String::new(),
        })
    })
}

fn relay_dir() -> PathBuf {
    if let Ok(path) = std::env::var("RELAY_STATE_PATH") {
        let candidate = Path::new(&path);
        if let Some(parent) = candidate.parent() {
            if !parent.as_os_str().is_empty() {
                return parent.to_path_buf();
            }
        }
    }
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        return PathBuf::from(local).join("RELAY");
    }
    if let Ok(home) = std::env::var("HOME") {
        return PathBuf::from(home).join(".local").join("share").join("RELAY");
    }
    PathBuf::from(".").join("RELAY")
}

fn bridge_file() -> PathBuf {
    relay_dir().join(BRIDGE_NAME)
}

fn write_private(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let mut options = fs::OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options.open(path).map_err(|error| error.to_string())?;
    file.write_all(bytes).map_err(|error| error.to_string())?;
    Ok(())
}

fn fill_token(buf: &mut [u8]) {
    #[cfg(unix)]
    {
        use std::io::Read;
        if let Ok(mut file) = std::fs::File::open("/dev/urandom") {
            let _ = file.read_exact(buf);
            return;
        }
    }
    #[cfg(windows)]
    {
        use windows::Win32::Security::Cryptography::{BCryptGenRandom, BCRYPT_USE_SYSTEM_PREFERRED_RNG};
        unsafe {
            let status = BCryptGenRandom(None, buf, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
            if status.0 != 0 {
                buf.fill(0);
            }
        }
    }
}

fn new_token() -> String {
    let mut bytes = [0u8; 32];
    fill_token(&mut bytes);
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

pub fn stop_activity_host() {
    stop_bridge();
    stop_observer();
}

#[tauri::command]
pub fn activity_observer_start(app: AppHandle) -> Result<Value, String> {
    start_observer(app)
}

#[tauri::command]
pub fn activity_observer_stop() -> Result<Value, String> {
    stop_observer();
    Ok(json!({ "state": "stopped" }))
}

#[tauri::command]
pub fn activity_observer_status() -> Result<Value, String> {
    Ok(json!({ "state": observer_state() }))
}

#[tauri::command]
pub fn activity_bridge_start(app: AppHandle) -> Result<Value, String> {
    let mut guard = host().lock().map_err(|error| error.to_string())?;
    if guard.running {
        return Ok(json!({ "state": "running", "port": guard.port }));
    }
    let listener = bind_loopback()?;
    let port = listener.local_addr().map_err(|error| error.to_string())?.port();
    let token = new_token();
    if token.chars().all(|ch| ch == '0') {
        return Err("token_entropy".into());
    }
    let stop = std::sync::Arc::new(AtomicBool::new(false));
    let stop_thread = stop.clone();
    let app_thread = app.clone();
    let token_for_thread = token.clone();
    thread::Builder::new()
        .name("relay-chrome-bridge".into())
        .spawn(move || bridge_loop(listener, token_for_thread, stop_thread, app_thread))
        .map_err(|error| error.to_string())?;
    fs::create_dir_all(relay_dir()).map_err(|error| error.to_string())?;
    write_private(bridge_file(), &serde_json::to_vec(&json!({ "port": port, "token": token })).map_err(|error| error.to_string())?)?;
    guard.running = true;
    guard.stop = Some(stop);
    guard.port = port;
    guard.token = token;
    Ok(json!({ "state": "running", "port": port }))
}

#[tauri::command]
pub fn activity_bridge_stop() -> Result<Value, String> {
    stop_bridge();
    Ok(json!({ "state": "stopped" }))
}

#[tauri::command]
pub fn activity_set_bridge_policy(policy: Value) -> Result<Value, String> {
    let mut guard = host().lock().map_err(|error| error.to_string())?;
    guard.policy = policy;
    Ok(json!({ "ok": true }))
}

fn stop_bridge() {
    if let Ok(mut guard) = host().lock() {
        if let Some(stop) = guard.stop.take() {
            stop.store(true, Ordering::SeqCst);
        }
        guard.running = false;
        let _ = fs::remove_file(bridge_file());
    }
}

fn bind_loopback() -> Result<TcpListener, String> {
    for port in 47_631u16..47_641 {
        if let Ok(listener) = TcpListener::bind(("127.0.0.1", port)) {
            listener.set_nonblocking(true).map_err(|error| error.to_string())?;
            return Ok(listener);
        }
    }
    Err("bridge_port_unavailable".into())
}

fn bridge_loop(listener: TcpListener, token: String, stop: std::sync::Arc<AtomicBool>, app: AppHandle) {
    while !stop.load(Ordering::SeqCst) {
        match listener.accept() {
            Ok((stream, addr)) => {
                if !addr.ip().is_loopback() {
                    continue;
                }
                let token = token.clone();
                let app = app.clone();
                thread::spawn(move || handle_client(stream, token, app));
            }
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                thread::sleep(Duration::from_millis(50));
            }
            Err(_) => thread::sleep(Duration::from_millis(50)),
        }
    }
}

fn handle_client(stream: std::net::TcpStream, token: String, app: AppHandle) {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(30)));
    let mut write = match stream.try_clone() {
        Ok(clone) => clone,
        Err(_) => return,
    };
    let reader = BufReader::new(stream);
    for line in reader.lines() {
        let Ok(line) = line else { break };
        if line.len() > relay_activity_bridge::MAX_MESSAGE_BYTES {
            let _ = writeln!(write, r#"{{"ok":false,"error":"oversized"}}"#);
            break;
        }
        let parsed: Value = match serde_json::from_str(&line) {
            Ok(value) => value,
            Err(_) => {
                let _ = writeln!(write, r#"{{"ok":false,"error":"malformed"}}"#);
                continue;
            }
        };
        if parsed.get("token").and_then(|item| item.as_str()) != Some(token.as_str()) {
            let _ = writeln!(write, r#"{{"ok":false,"error":"token"}}"#);
            continue;
        }
        let Some(body) = parsed.get("body") else {
            let _ = writeln!(write, r#"{{"ok":false,"error":"malformed"}}"#);
            continue;
        };
        let bytes = serde_json::to_vec(body).unwrap_or_default();
        let policy = host().lock().ok().map(|guard| guard.policy.clone()).unwrap_or(json!({}));
        match validate_client_json(&bytes) {
            Ok(ClientMessage::Hello) => {
                let _ = app.emit(
                    "activity-observation",
                    json!({ "kind": "chrome-status", "status": "connected" }),
                );
                let _ = writeln!(write, "{}", json!({ "ok": true, "policy": policy }));
            }
            Ok(ClientMessage::Observation(value)) => {
                let observation = value.get("observation").cloned().unwrap_or(Value::Null);
                let _ = app.emit(
                    "activity-observation",
                    json!({ "kind": "observation", "observation": observation }),
                );
                let _ = writeln!(write, "{}", json!({ "ok": true, "policy": policy }));
            }
            Err(reason) => {
                let _ = writeln!(write, "{}", json!({ "ok": false, "error": reason }));
            }
        }
    }
}

fn observer_state() -> &'static str {
    if observer_running() {
        "running"
    } else if cfg!(windows) {
        "stopped"
    } else {
        "unavailable"
    }
}

#[cfg(not(windows))]
fn start_observer(_app: AppHandle) -> Result<Value, String> {
    Ok(json!({ "state": "unavailable", "reason": "windows_only" }))
}

#[cfg(not(windows))]
fn stop_observer() {}

#[cfg(not(windows))]
fn observer_running() -> bool {
    false
}

#[cfg(windows)]
fn observer_running() -> bool {
    relay_windows_observer::running()
}

#[cfg(windows)]
fn start_observer(app: AppHandle) -> Result<Value, String> {
    remember_app(app);
    relay_windows_observer::start(emit_foreground)?;
    Ok(json!({ "state": "running" }))
}

#[cfg(windows)]
fn stop_observer() {
    relay_windows_observer::stop();
}

#[cfg(windows)]
static APP: OnceLock<Mutex<Option<AppHandle>>> = OnceLock::new();

#[cfg(windows)]
fn remember_app(app: AppHandle) {
    let slot = APP.get_or_init(|| Mutex::new(None));
    if let Ok(mut guard) = slot.lock() {
        *guard = Some(app);
    }
}

#[cfg(windows)]
fn emit_foreground(capture: &'static str, process: &str, executable: &str, title: &str) {
    let Some(app) = APP.get().and_then(|slot| slot.lock().ok()).and_then(|guard| guard.clone()) else {
        return;
    };
    let provider = if capture == "poll" { "foreground-poll" } else { "setwineventhook" };
    let _ = app.emit(
        "activity-observation",
        json!({
            "kind": "observation",
            "observation": {
                "timestamp": now_iso(),
                "source": { "type": "windows", "provider": provider },
                "eventType": "foreground.changed",
                "application": {
                    "processName": process,
                    "executable": executable,
                    "windowTitle": title
                },
                "data": { "capture": capture }
            }
        }),
    );
}

