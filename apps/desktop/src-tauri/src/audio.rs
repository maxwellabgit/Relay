use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::VecDeque;
use std::io::{BufRead, BufReader};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::{Mutex, OnceLock};
use std::thread;
use std::time::Duration;

const MAX_QUEUE: usize = 64;

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

fn module_dir() -> Option<PathBuf> {
    if let Ok(value) = std::env::var("RELAY_AUDIO_MODULE_DIR") {
        let path = PathBuf::from(value);
        if path.is_dir() {
            return Some(path);
        }
    }
    // Dev layout: <repo>/apps/desktop/src-tauri → ../../../tools/audio
    let exe = std::env::current_exe().ok()?;
    let mut cursor = exe.parent()?.to_path_buf();
    for _ in 0..8 {
        let candidate = cursor.join("tools").join("audio");
        if candidate.join("relay_audio").join("live.py").is_file() {
            return Some(candidate);
        }
        if !cursor.pop() {
            break;
        }
    }
    None
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

fn spawn_reader(child_stdout: std::process::ChildStdout) {
    thread::spawn(move || {
        let reader = BufReader::new(child_stdout);
        for line in reader.lines().map_while(Result::ok) {
            let trimmed = line.trim();
            if trimmed.is_empty() {
                continue;
            }
            let parsed: Result<Value, _> = serde_json::from_str(trimmed);
            let Ok(value) = parsed else {
                continue;
            };
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
        if let Ok(mut guard) = state().lock() {
            guard.healthy = false;
            guard.capturing = false;
            guard.detail = "source_exited".into();
            guard.child = None;
        }
    });
}

fn kill_child(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

#[tauri::command]
pub fn audio_start(request: AudioStartRequest) -> Result<AudioStatus, String> {
    let mut guard = state().lock().map_err(|_| "audio_lock".to_string())?;
    if guard.capturing {
        return Ok(AudioStatus {
            ok: guard.healthy,
            detail: guard.detail.clone(),
            capturing: true,
        });
    }
    let module = module_dir().ok_or_else(|| "audio_module_missing".to_string())?;
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
    spawn_reader(stdout);
    guard.child = Some(child);
    guard.capturing = true;
    guard.healthy = true;
    guard.detail = "capturing".into();
    Ok(AudioStatus {
        ok: true,
        detail: "capturing".into(),
        capturing: true,
    })
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
            ok: guard.healthy || !guard.capturing,
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
}
