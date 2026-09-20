#[tauri::command]
pub fn secret_status() -> String {
    match std::env::var("RELAY_TYPESAFE_API_KEY") {
        Ok(value) if !value.trim().is_empty() => "present".into(),
        _ => "disabled".into(),
    }
}
