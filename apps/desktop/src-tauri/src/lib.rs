mod commands;
mod diagnostics;
mod halo;
mod hotkeys;
mod secrets;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .invoke_handler(tauri::generate_handler![
            commands::ping,
            secrets::secret_status,
            diagnostics::trace_run_dir,
            diagnostics::append_trace_event,
            diagnostics::read_trace_events,
            hotkeys::hotkey_status,
            halo::halo_status
        ])
        .run(tauri::generate_context!())
        .expect("error while running RELAY desktop");
}
