#[tauri::command]
pub fn secret_status() -> String {
    "disabled".into()
}
