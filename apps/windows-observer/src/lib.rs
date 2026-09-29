//! Foreground changes use `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`.
//! A one-second thread timer re-reads the foreground title because
//! `EVENT_OBJECT_NAMECHANGE` fires for every UI element in the session.
//! If the hook cannot be installed, that timer is the poll fallback.
//! Identical process and title samples are not emitted.

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::thread::{self, JoinHandle};
use windows::Win32::Foundation::{CloseHandle, LPARAM, WPARAM};
use windows::Win32::System::Threading::{
    GetCurrentThreadId, OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetForegroundWindow, GetMessageW, GetWindowTextW, GetWindowThreadProcessId, KillTimer,
    PostThreadMessageW, SetTimer, TranslateMessage, EVENT_SYSTEM_FOREGROUND, MSG, WINEVENT_OUTOFCONTEXT, WM_QUIT,
    WM_TIMER,
};

pub type EmitForeground = fn(capture: &'static str, process: &str, executable: &str, title: &str);

struct Control {
    stop: AtomicBool,
    thread: AtomicU32,
    running: AtomicBool,
}

fn control() -> &'static Control {
    static CONTROL: OnceLock<Control> = OnceLock::new();
    CONTROL.get_or_init(|| Control {
        stop: AtomicBool::new(false),
        thread: AtomicU32::new(0),
        running: AtomicBool::new(false),
    })
}

fn emit_slot() -> &'static Mutex<Option<EmitForeground>> {
    static EMIT: OnceLock<Mutex<Option<EmitForeground>>> = OnceLock::new();
    EMIT.get_or_init(|| Mutex::new(None))
}

fn last() -> &'static Mutex<(String, String)> {
    static LAST: OnceLock<Mutex<(String, String)>> = OnceLock::new();
    LAST.get_or_init(|| Mutex::new((String::new(), String::new())))
}

fn generation() -> &'static AtomicU64 {
    static GEN: OnceLock<AtomicU64> = OnceLock::new();
    GEN.get_or_init(|| AtomicU64::new(0))
}

fn join_slot() -> &'static Mutex<Option<JoinHandle<()>>> {
    static JOIN: OnceLock<Mutex<Option<JoinHandle<()>>>> = OnceLock::new();
    JOIN.get_or_init(|| Mutex::new(None))
}

pub fn running() -> bool {
    control().running.load(Ordering::SeqCst)
}

pub fn start(emit: EmitForeground) -> Result<(), String> {
    stop();
    if let Ok(mut slot) = join_slot().lock() {
        if let Some(handle) = slot.take() {
            let _ = handle.join();
        }
    }
    if let Ok(mut guard) = emit_slot().lock() {
        *guard = Some(emit);
    }
    let gen = generation().fetch_add(1, Ordering::SeqCst) + 1;
    control().stop.store(false, Ordering::SeqCst);
    control().running.store(true, Ordering::SeqCst);
    let handle = thread::Builder::new()
        .name("relay-windows-observer".into())
        .spawn(move || run(gen))
        .map_err(|error| error.to_string())?;
    if let Ok(mut slot) = join_slot().lock() {
        *slot = Some(handle);
    }
    Ok(())
}

pub fn stop() {
    control().stop.store(true, Ordering::SeqCst);
    let thread_id = control().thread.load(Ordering::SeqCst);
    if thread_id != 0 {
        unsafe {
            let _ = PostThreadMessageW(thread_id, WM_QUIT, WPARAM(0), LPARAM(0));
        }
    }
}

fn run(gen: u64) {
    unsafe {
        control().thread.store(GetCurrentThreadId(), Ordering::SeqCst);
        let hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            None,
            Some(on_event),
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        );
        let timer = SetTimer(None, 1, 1000, None);
        let mut message = MSG::default();
        loop {
            let status = GetMessageW(&mut message, None, 0, 0).0;
            if status == 0 || status == -1 {
                break;
            }
            if message.message == WM_TIMER {
                let capture = if hook.is_invalid() { "poll" } else { "title" };
                emit_sample(capture);
            }
            if message.message == WM_QUIT || control().stop.load(Ordering::SeqCst) {
                break;
            }
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if !hook.is_invalid() {
            let _ = UnhookWinEvent(hook);
        }
        if timer != 0 {
            let _ = KillTimer(None, timer);
        }
    }
    if generation().load(Ordering::SeqCst) == gen {
        control().running.store(false, Ordering::SeqCst);
        control().thread.store(0, Ordering::SeqCst);
    }
}

unsafe extern "system" fn on_event(
    _hook: HWINEVENTHOOK,
    event: u32,
    hwnd: windows::Win32::Foundation::HWND,
    _object: i32,
    _child: i32,
    _thread: u32,
    _time: u32,
) {
    if event != EVENT_SYSTEM_FOREGROUND || control().stop.load(Ordering::SeqCst) || hwnd.is_invalid() {
        return;
    }
    emit_sample("event");
}

fn emit_sample(capture: &'static str) {
    let Some((process, executable, title)) = foreground() else { return };
    let mut guard = match last().lock() {
        Ok(guard) => guard,
        Err(_) => return,
    };
    if guard.0 == process && guard.1 == title {
        return;
    }
    *guard = (process.clone(), title.clone());
    drop(guard);
    let emit = emit_slot().lock().ok().and_then(|guard| *guard);
    if let Some(emit) = emit {
        emit(capture, &process, &executable, &title);
    }
}

fn foreground() -> Option<(String, String, String)> {
    unsafe {
        let hwnd = GetForegroundWindow();
        if hwnd.is_invalid() {
            return None;
        }
        let mut buffer = [0u16; 512];
        let copied = GetWindowTextW(hwnd, &mut buffer);
        let title = String::from_utf16_lossy(&buffer[..copied.max(0) as usize]).trim().to_string();
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(std::ptr::addr_of_mut!(pid)));
        let (process, executable) = process_name(pid).unwrap_or_else(|| ("unknown".into(), String::new()));
        if process == "unknown" && title.is_empty() {
            return None;
        }
        Some((process, executable, title))
    }
}

fn process_name(pid: u32) -> Option<(String, String)> {
    if pid == 0 {
        return None;
    }
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buffer = [0u16; 1024];
        let mut len = buffer.len() as u32;
        let queried = QueryFullProcessImageNameW(handle, PROCESS_NAME_WIN32, windows::core::PWSTR(buffer.as_mut_ptr()), &mut len);
        let _ = CloseHandle(handle);
        queried.ok()?;
        let path = String::from_utf16_lossy(&buffer[..len as usize]);
        let name = path.rsplit(['\\', '/']).next().unwrap_or("unknown").to_string();
        Some((name, path))
    }
}
