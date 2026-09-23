#[tauri::command]
pub fn hotkey_status() -> String {
    // No global hotkey is registered. Listen stays an on-screen control.
    "unregistered".into()
}
