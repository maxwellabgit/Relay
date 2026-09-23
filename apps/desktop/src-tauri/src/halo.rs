#[tauri::command]
pub fn halo_status() -> String {
    // Official emulator and physical BLE are not connected in this build.
    "disabled".into()
}
