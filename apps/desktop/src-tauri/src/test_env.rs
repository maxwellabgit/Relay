use std::sync::{Mutex, MutexGuard};

static ENV_LOCK: Mutex<()> = Mutex::new(());

pub fn lock_env() -> MutexGuard<'static, ()> {
    ENV_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

pub fn with_temp_localappdata<T>(label: &str, body: impl FnOnce(&std::path::Path) -> T) -> T {
    let _guard = lock_env();
    let previous = std::env::var_os("LOCALAPPDATA");
    let dir = std::env::temp_dir().join(format!(
        "relay-{label}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0)
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("temp LOCALAPPDATA");
    std::env::set_var("LOCALAPPDATA", &dir);
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| body(&dir)));
    let _ = std::fs::remove_dir_all(&dir);
    match previous {
        Some(value) => std::env::set_var("LOCALAPPDATA", value),
        None => std::env::remove_var("LOCALAPPDATA"),
    }
    match result {
        Ok(value) => value,
        Err(payload) => std::panic::resume_unwind(payload),
    }
}
