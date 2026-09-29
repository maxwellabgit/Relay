use serde::Serialize;
use std::sync::{Mutex, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use windows::Win32::Foundation::{CloseHandle, HWND};
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetForegroundWindow, GetWindowTextW, GetWindowThreadProcessId, PeekMessageW,
    TranslateMessage, EVENT_SYSTEM_FOREGROUND, MSG, PM_REMOVE, WINEVENT_OUTOFCONTEXT,
    WINEVENT_SKIPOWNPROCESS, WM_QUIT,
};

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FocusSignal {
    pub observed_at_ms: u64,
    pub application: String,
    pub title: String,
    pub duration_ms: u64,
    pub process_id: u32,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ObserverStatus {
    pub running: bool,
    pub detail: String,
    pub hook: bool,
}

#[derive(Clone)]
struct OpenSpan {
    at_ms: u64,
    application: String,
    title: String,
    process_id: u32,
}

struct ObserverState {
    stop: bool,
    hook_ok: bool,
    detail: String,
    open: Option<OpenSpan>,
    closed: Vec<FocusSignal>,
    thread: Option<JoinHandle<()>>,
}

fn state() -> &'static Mutex<ObserverState> {
    static STATE: OnceLock<Mutex<ObserverState>> = OnceLock::new();
    STATE.get_or_init(|| {
        Mutex::new(ObserverState {
            stop: true,
            hook_ok: false,
            detail: "Windows observation is off.".into(),
            open: None,
            closed: Vec::new(),
            thread: None,
        })
    })
}

pub fn status() -> ObserverStatus {
    let guard = state().lock().expect("observer");
    ObserverStatus {
        running: !guard.stop,
        detail: guard.detail.clone(),
        hook: guard.hook_ok,
    }
}

pub fn drain() -> Vec<FocusSignal> {
    let mut guard = state().lock().expect("observer");
    let mut out = std::mem::take(&mut guard.closed);
    if let Some(open) = &guard.open {
        out.push(FocusSignal {
            observed_at_ms: open.at_ms,
            application: open.application.clone(),
            title: open.title.clone(),
            duration_ms: now_ms().saturating_sub(open.at_ms),
            process_id: open.process_id,
        });
    }
    out
}

pub fn start() -> Result<(), String> {
    let mut guard = state().lock().expect("observer");
    if !guard.stop {
        return Ok(());
    }
    guard.stop = false;
    guard.detail = "Watching the foreground window.".into();
    let handle = thread::Builder::new()
        .name("relay-window-observer".into())
        .spawn(|| unsafe { run_loop() })
        .map_err(|error| error.to_string())?;
    guard.thread = Some(handle);
    Ok(())
}

pub fn stop() {
    let handle = {
        let mut guard = state().lock().expect("observer");
        guard.stop = true;
        guard.detail = "Windows observation is off.".into();
        guard.open = None;
        guard.thread.take()
    };
    if let Some(handle) = handle {
        let _ = handle.join();
    }
}

unsafe fn run_loop() {
    let hook = SetWinEventHook(
        EVENT_SYSTEM_FOREGROUND,
        EVENT_SYSTEM_FOREGROUND,
        None,
        Some(on_foreground),
        0,
        0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
    );
    {
        let mut guard = state().lock().expect("observer");
        guard.hook_ok = !hook.is_invalid();
        if hook.is_invalid() {
            guard.detail = "Foreground hook unavailable. Sampling the active window.".into();
        }
    }
    sample();
    while !state().lock().expect("observer").stop {
        let mut message = MSG::default();
        while PeekMessageW(&mut message, None, 0, 0, PM_REMOVE).as_bool() {
            if message.message == WM_QUIT {
                break;
            }
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        sample();
        thread::sleep(Duration::from_millis(400));
    }
    if !hook.is_invalid() {
        let _ = UnhookWinEvent(hook);
    }
}

unsafe extern "system" fn on_foreground(
    _hook: HWINEVENTHOOK,
    _event: u32,
    _hwnd: HWND,
    _object: i32,
    _child: i32,
    _thread: u32,
    _time: u32,
) {
    sample();
}

fn sample() {
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.0.is_null() {
        return;
    }
    let mut pid = 0u32;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    if pid == 0 || pid == std::process::id() {
        return;
    }
    let title = window_title(hwnd);
    let application = process_name(pid);
    let title = redact_title(&title);
    let mut guard = state().lock().expect("observer");
    if guard.stop {
        return;
    }
    let previous = guard.open.clone();
    if let Some(open) = previous {
        if open.process_id == pid && open.title == title && open.application == application {
            return;
        }
        guard.closed.push(FocusSignal {
            observed_at_ms: open.at_ms,
            application: open.application,
            title: open.title,
            duration_ms: now_ms().saturating_sub(open.at_ms),
            process_id: open.process_id,
        });
        if guard.closed.len() > 200 {
            let extra = guard.closed.len() - 200;
            guard.closed.drain(0..extra);
        }
    }
    guard.open = Some(OpenSpan {
        at_ms: now_ms(),
        application,
        title,
        process_id: pid,
    });
}

fn window_title(hwnd: HWND) -> String {
    let mut buffer = [0u16; 512];
    let length = unsafe { GetWindowTextW(hwnd, &mut buffer) };
    if length <= 0 {
        return String::new();
    }
    String::from_utf16_lossy(&buffer[..length as usize])
        .trim()
        .to_string()
}

fn process_name(pid: u32) -> String {
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        let Ok(handle) = handle else {
            return format!("pid-{pid}");
        };
        let mut buffer = [0u16; 512];
        let mut size = buffer.len() as u32;
        let ok = QueryFullProcessImageNameW(
            handle,
            PROCESS_NAME_WIN32,
            windows::core::PWSTR(buffer.as_mut_ptr()),
            &mut size,
        );
        let _ = CloseHandle(handle);
        if ok.is_err() || size == 0 {
            return format!("pid-{pid}");
        }
        let full = String::from_utf16_lossy(&buffer[..size as usize]);
        full.rsplit(['\\', '/'])
            .next()
            .unwrap_or("unknown")
            .to_string()
    }
}

fn redact_title(title: &str) -> String {
    let lower = title.to_lowercase();
    if lower.contains("password") || lower.contains("passcode") || lower.contains("one-time code") {
        return "[redacted]".into();
    }
    title.chars().take(500).collect()
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}
