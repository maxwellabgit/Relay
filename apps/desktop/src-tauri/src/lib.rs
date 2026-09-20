mod commands;
mod diagnostics;
mod halo;
mod hotkeys;
mod secrets;
mod state;
mod typesafe;

use std::sync::Mutex;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let db = state::StateDb::open_default().expect("open RELAY state database");
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .manage(Mutex::new(db))
        .invoke_handler(tauri::generate_handler![
            commands::ping,
            secrets::secret_status,
            diagnostics::trace_run_dir,
            diagnostics::append_trace_event,
            diagnostics::read_trace_events,
            diagnostics::open_run_folder,
            typesafe::typesafe_judge,
            hotkeys::hotkey_status,
            halo::halo_status,
            state::store_execute
        ])
        .run(tauri::generate_context!())
        .expect("error while running RELAY desktop");
}
