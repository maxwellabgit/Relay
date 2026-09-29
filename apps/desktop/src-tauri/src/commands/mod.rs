#[tauri::command]
pub fn ping() -> String {
    "pong".into()
}

/// True only for an isolated headed test process. Release launches leave this off.
#[tauri::command]
pub fn e2e_fixture_enabled() -> bool {
    std::env::var("RELAY_E2E").ok().as_deref() == Some("1")
}

#[tauri::command]
pub fn observation_start() -> Result<(), String> {
    crate::observe::start()
}

#[tauri::command]
pub fn observation_stop() {
    crate::observe::stop();
}

#[tauri::command]
pub fn observation_status() -> crate::observe::ObserverStatus {
    crate::observe::status()
}

#[tauri::command]
pub fn observation_drain() -> Vec<crate::observe::FocusSignal> {
    crate::observe::drain()
}

#[tauri::command]
pub fn bridge_set_accept(accept: bool) -> Result<(), String> {
    crate::bridge::set_accept(accept).map_err(|error| error.to_string())
}

#[tauri::command]
pub fn bridge_drain() -> Result<Vec<serde_json::Value>, String> {
    crate::bridge::drain_inbox()
}

#[tauri::command]
pub fn bridge_status() -> serde_json::Value {
    let (connected, detail) = crate::bridge::bridge_status();
    serde_json::json!({ "connected": connected, "detail": detail })
}

#[tauri::command]
pub fn workflow_read_text(path: String) -> Result<String, String> {
    crate::bridge::read_allowed_text(&path)
}

#[tauri::command]
pub fn workflow_write_draft(name: String, body: String) -> Result<String, String> {
    crate::bridge::write_draft(&name, &body)
}

#[tauri::command]
pub fn workflow_remember_roots(folders: Vec<String>, master: Option<String>) -> Result<(), String> {
    crate::bridge::remember_roots(&folders, master.as_deref()).map_err(|error| error.to_string())
}
