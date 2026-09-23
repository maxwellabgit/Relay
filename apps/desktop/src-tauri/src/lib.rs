mod artifacts;
mod audio;
mod commands;
mod diagnostics;
mod grants;
mod halo;
mod hotkeys;
mod local_model;
mod secrets;
mod state;
mod typesafe;

#[cfg(test)]
mod test_env;

use std::sync::Mutex;
use tauri::RunEvent;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    secrets::maybe_seed_from_env();
    let db = state::StateDb::open_default().expect("open RELAY state database");
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .manage(Mutex::new(db))
        .invoke_handler(tauri::generate_handler![
            commands::ping,
            secrets::secret_status,
            secrets::secret_import_staging_file,
            secrets::secret_delete,
            artifacts::artifact_put,
            artifacts::artifact_get,
            artifacts::artifact_provenance,
            local_model::local_model_status,
            local_model::local_model_generate,
            audio::audio_start,
            audio::audio_stop,
            audio::audio_status,
            audio::audio_drain,
            diagnostics::trace_run_dir,
            diagnostics::complete_trace_run,
            diagnostics::append_trace_event,
            diagnostics::write_live_summary,
            diagnostics::read_trace_events,
            diagnostics::open_run_folder,
            diagnostics::share_diagnostics,
            diagnostics::diagnostics_latest_path,
            typesafe::typesafe_judge,
            typesafe::typesafe_cancel,
            diagnostics::write_canary_receipt,
            hotkeys::hotkey_status,
            halo::halo_status,
            state::store_execute
        ])
        .build(tauri::generate_context!())
        .expect("error while building RELAY desktop")
        .run(|_app, event| {
            if let RunEvent::Exit = event {
                diagnostics::complete_active_run();
            }
        });
}
