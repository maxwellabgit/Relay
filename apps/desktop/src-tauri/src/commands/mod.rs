#[tauri::command]
pub fn ping() -> String {
    "pong".into()
}

/// True only for an isolated headed test process. Release launches leave this off.
#[tauri::command]
pub fn e2e_fixture_enabled() -> bool {
    std::env::var("RELAY_E2E").ok().as_deref() == Some("1")
}
