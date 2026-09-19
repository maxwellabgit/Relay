#[tauri::command]
pub fn ping() -> String {
    "pong".into()
}
