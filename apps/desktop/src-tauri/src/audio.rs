use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::VecDeque;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Mutex, OnceLock};
use std::thread;
use std::time::Duration;
use tauri::{AppHandle, Manager};

const MAX_QUEUE: usize = 64;
const STARTUP_TIMEOUT: Duration = Duration::from_secs(12);

#[derive(Default)]
struct AudioState {
    child: Option<Child>,
    queue: VecDeque<Value>,
    healthy: bool,
    detail: String,
    capturing: bool,
}

fn state() -> &'static Mutex<AudioState> {
    static STATE: OnceLock<Mutex<AudioState>> = OnceLock::new();
    STATE.get_or_init(|| {
        Mutex::new(AudioState {
            healthy: true,
            detail: "idle".into(),
            ..AudioState::default()
        })
    })
}

#[derive(Deserialize)]
pub struct AudioStartRequest {
    pub session_id: String,
}

#[derive(Serialize)]
pub struct AudioStatus {
    pub ok: bool,
    pub detail: String,
    pub capturing: bool,
}

#[derive(Serialize)]
pub struct AudioDrainResult {
    pub events: Vec<Value>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum StartupSignal {
    Ready,
    Error(String),
    Exited,
}

fn is_audio_module(path: &Path) -> bool {
    path.join("relay_audio").join("live.py").is_file()
}

/// Resolve the relay_audio module directory.
///
/// Packaged builds use the Tauri resource dir only.
/// Dev builds may use `RELAY_AUDIO_MODULE_DIR` or walk upward for `tools/audio`.
pub fn resolve_module_dir(
    packaged: bool,
    resource_dir: Option<&Path>,
    env_override: Option<&Path>,
    walk_from: Option<&Path>,
) -> Option<PathBuf> {
    if packaged {
        let resource = resource_dir?;
        let candidate = resource.join("tools").join("audio");
        return is_audio_module(&candidate).then_some(candidate);
    }

    if let Some(path) = env_override {
        if is_audio_module(path) {
            return Some(path.to_path_buf());
        }
    }

    if let Some(start) = walk_from {
        let mut cursor = start.to_path_buf();
        for _ in 0..8 {
            let candidate = cursor.join("tools").join("audio");
            if is_audio_module(&candidate) {
                return Some(candidate);
            }
            if !cursor.pop() {
                break;
            }
        }
    }

    None
}

fn module_dir(app: &AppHandle) -> Option<PathBuf> {
    let packaged = !cfg!(debug_assertions);
    let resource_dir = app.path().resource_dir().ok();
    let env_override = std::env::var("RELAY_AUDIO_MODULE_DIR")
        .ok()
        .map(PathBuf::from);
    let walk_from = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|p| p.to_path_buf()));

    resolve_module_dir(
        packaged,
        resource_dir.as_deref(),
        env_override.as_deref(),
        walk_from.as_deref(),
    )
}

fn validate_event(value: &Value) -> bool {
    let typ = value.get("type").and_then(|v| v.as_str()).unwrap_or("");
    if typ != "segment.final" {
        return false;
    }
    let segment = match value.get("segment") {
        Some(s) => s,
        None => return false,
    };
    if segment.get("final").and_then(|v| v.as_bool()) != Some(true) {
        return false;
    }
    let text = segment.get("text").and_then(|v| v.as_str()).unwrap_or("");
    if text.is_empty() || text.len() > 8_000 {
        return false;
    }
    let speaker = segment.get("speakerKey");
    if speaker.map(|v| !v.is_null()).unwrap_or(false) {
        return false;
    }
    true
}

fn control_signal(value: &Value) -> Option<StartupSignal> {
    let typ = value.get("type").and_then(|v| v.as_str()).unwrap_or("");
    match typ {
        "source.ready" => Some(StartupSignal::Ready),
        "source.error" => {
            let code = value
                .get("code")
                .and_then(|v| v.as_str())
                .unwrap_or("asr_unavailable")
                .to_string();
            Some(StartupSignal::Error(code))
        }
        _ => None,
    }
}

fn spawn_reader(child_stdout: std::process::ChildStdout, startup_tx: Sender<StartupSignal>) {
    thread::spawn(move || {
        let reader = BufReader::new(child_stdout);
        let mut startup_tx = Some(startup_tx);
        for line in reader.lines().map_while(Result::ok) {
            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }
            let parsed: Result<Value, _> = serde_json::from_str(trimmed);
            let Ok(value) = parsed else {
                continue;
            };
            if let Some(signal) = control_signal(&value) {
                if let Some(tx) = startup_tx.take() {
                    let _ = tx.send(signal);
                }
                continue;
            }
            if !validate_event(&value) {
                continue;
            }
            if let Ok(mut guard) = state().lock() {
                if guard.queue.len() >= MAX_QUEUE {
                    guard.queue.pop_front();
                }
                guard.queue.push_back(value);
            }
        }
        if let Some(tx) = startup_tx.take() {
            let _ = tx.send(StartupSignal::Exited);
        }
        if let Ok(mut guard) = state().lock() {
            guard.healthy = false;
            guard.capturing = false;
            if guard.detail == "capturing" || guard.detail == "starting" {
                guard.detail = "source_exited".into();
            }
            guard.child = None;
        }
    });
}

fn kill_child(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

fn wait_for_startup(rx: Receiver<StartupSignal>) -> StartupSignal {
    match rx.recv_timeout(STARTUP_TIMEOUT) {
        Ok(signal) => signal,
        Err(RecvTimeoutError::Timeout) => StartupSignal::Error("source_timeout".into()),
        Err(RecvTimeoutError::Disconnected) => StartupSignal::Exited,
    }
}

fn fail_start(detail: &str) -> AudioStatus {
    if let Ok(mut guard) = state().lock() {
        if let Some(mut child) = guard.child.take() {
            kill_child(&mut child);
        }
        guard.capturing = false;
        guard.healthy = false;
        guard.detail = detail.to_string();
    }
    AudioStatus {
        ok: false,
        detail: detail.to_string(),
        capturing: false,
    }
}

#[tauri::command]
pub fn audio_start(app: AppHandle, request: AudioStartRequest) -> Result<AudioStatus, String> {
    {
        let guard = state().lock().map_err(|_| "audio_lock".to_string())?;
        if guard.capturing {
            return Ok(AudioStatus {
                ok: guard.healthy,
                detail: guard.detail.clone(),
                capturing: true,
            });
        }
    }

    let module = module_dir(&app).ok_or_else(|| "audio_module_missing".to_string())?;
    let python = std::env::var("RELAY_PYTHON")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .unwrap_or_else(|| "python".into());

    let mut command = Command::new(&python);
    command
        .arg("-m")
        .arg("relay_audio.live")
        .arg("--session-id")
        .arg(&request.session_id)
        .current_dir(&module)
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .stdin(Stdio::null());

    if let Ok(fixture) = std::env::var("RELAY_AUDIO_FIXTURE") {
        if !fixture.trim().is_empty() {
            command.arg("--fixture").arg(fixture);
        }
    }

    let mut child = command
        .spawn()
        .map_err(|_| "audio_spawn_failed".to_string())?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| "audio_stdout".to_string())?;
    let (tx, rx) = mpsc::channel();
    spawn_reader(stdout, tx);

    {
        let mut guard = state().lock().map_err(|_| "audio_lock".to_string())?;
        guard.child = Some(child);
        guard.capturing = false;
        guard.healthy = false;
        guard.detail = "starting".into();
    }

    let signal = wait_for_startup(rx);
    match signal {
        StartupSignal::Ready => {
            let mut guard = state().lock().map_err(|_| "audio_lock".to_string())?;
            guard.capturing = true;
            guard.healthy = true;
            guard.detail = "capturing".into();
            Ok(AudioStatus {
                ok: true,
                detail: "capturing".into(),
                capturing: true,
            })
        }
        StartupSignal::Error(code) => Ok(fail_start(&code)),
        StartupSignal::Exited => Ok(fail_start("asr_unavailable")),
    }
}

#[tauri::command]
pub fn audio_stop() -> Result<AudioStatus, String> {
    let mut guard = state().lock().map_err(|_| "audio_lock".to_string())?;
    if let Some(mut child) = guard.child.take() {
        kill_child(&mut child);
    }
    guard.capturing = false;
    guard.healthy = true;
    guard.detail = "idle".into();
    // Give reader a moment; queue retained for final drain by caller.
    drop(guard);
    thread::sleep(Duration::from_millis(20));
    Ok(AudioStatus {
        ok: true,
        detail: "idle".into(),
        capturing: false,
    })
}

#[tauri::command]
pub fn audio_status() -> AudioStatus {
    match state().lock() {
        Ok(guard) => AudioStatus {
            ok: guard.healthy,
            detail: guard.detail.clone(),
            capturing: guard.capturing,
        },
        Err(_) => AudioStatus {
            ok: false,
            detail: "audio_lock".into(),
            capturing: false,
        },
    }
}

#[tauri::command]
pub fn audio_drain() -> AudioDrainResult {
    let mut events = Vec::new();
    if let Ok(mut guard) = state().lock() {
        while let Some(event) = guard.queue.pop_front() {
            events.push(event);
        }
    }
    AudioDrainResult { events }
}

/// Test helper: enqueue a validated final segment without spawning a process.
#[cfg(test)]
pub fn audio_inject_for_test(event: Value) -> bool {
    if !validate_event(&event) {
        return false;
    }
    if let Ok(mut guard) = state().lock() {
        guard.queue.push_back(event);
        guard.healthy = true;
        return true;
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_audio_module() -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!("relay_audio_mod_{nanos}"));
        let live = root.join("relay_audio");
        fs::create_dir_all(&live).unwrap();
        fs::write(live.join("live.py"), b"# test\n").unwrap();
        root
    }

    #[test]
    fn drain_returns_validated_finals_only() {
        let _ = audio_stop();
        assert!(audio_inject_for_test(json!({
            "v": 1,
            "type": "segment.final",
            "atMs": 1000,
            "segment": {
                "schemaVersion": 1,
                "sourceId": "test",
                "sessionId": "s1",
                "segmentId": "seg_1",
                "revision": 1,
                "sequence": 1,
                "startMs": 0,
                "endMs": 1000,
                "speakerKey": null,
                "speakerConfidence": null,
                "text": "hello relay",
                "textConfidence": null,
                "final": true,
                "origin": "microphone",
                "cursor": null
            }
        })));
        assert!(!audio_inject_for_test(json!({
            "type": "segment.interim",
            "segment": { "text": "partial", "final": false }
        })));
        let drained = audio_drain();
        assert_eq!(drained.events.len(), 1);
        assert_eq!(
            drained.events[0]["segment"]["text"].as_str().unwrap(),
            "hello relay"
        );
    }

    #[test]
    fn packaged_resolve_uses_resource_dir_only() {
        let module = temp_audio_module();
        let resource = module.parent().unwrap().join("resource_root");
        let bundled = resource.join("tools").join("audio");
        fs::create_dir_all(bundled.join("relay_audio")).unwrap();
        fs::write(bundled.join("relay_audio").join("live.py"), b"# bundled\n").unwrap();

        let env_override = temp_audio_module();
        let resolved = resolve_module_dir(
            true,
            Some(resource.as_path()),
            Some(env_override.as_path()),
            Some(env_override.as_path()),
        );
        assert_eq!(resolved.as_deref(), Some(bundled.as_path()));

        let missing = resolve_module_dir(
            true,
            Some(std::env::temp_dir().as_path()),
            Some(env_override.as_path()),
            Some(env_override.as_path()),
        );
        assert!(missing.is_none());

        let _ = fs::remove_dir_all(&module);
        let _ = fs::remove_dir_all(&resource);
        let _ = fs::remove_dir_all(&env_override);
    }

    #[test]
    fn dev_resolve_prefers_env_then_walk() {
        let env_module = temp_audio_module();
        let walk_root = std::env::temp_dir().join(format!(
            "relay_audio_walk_{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let walked = walk_root.join("tools").join("audio");
        fs::create_dir_all(walked.join("relay_audio")).unwrap();
        fs::write(walked.join("relay_audio").join("live.py"), b"# walk\n").unwrap();

        let via_env = resolve_module_dir(
            false,
            None,
            Some(env_module.as_path()),
            Some(walk_root.as_path()),
        );
        assert_eq!(via_env.as_deref(), Some(env_module.as_path()));

        let via_walk = resolve_module_dir(false, None, None, Some(walk_root.as_path()));
        assert_eq!(via_walk.as_deref(), Some(walked.as_path()));

        let _ = fs::remove_dir_all(&env_module);
        let _ = fs::remove_dir_all(&walk_root);
    }

    #[test]
    fn control_signal_parses_ready_and_error() {
        assert_eq!(
            control_signal(
                &json!({"v":1,"type":"source.ready","backend":"faster-whisper","sampleRate":16000})
            ),
            Some(StartupSignal::Ready)
        );
        assert_eq!(
            control_signal(&json!({"v":1,"type":"source.error","code":"asr_unavailable"})),
            Some(StartupSignal::Error("asr_unavailable".into()))
        );
        assert_eq!(control_signal(&json!({"v":1,"type":"segment.final"})), None);
    }
}
