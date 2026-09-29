fn main() {
    if let Err(error) = relay_desktop_lib::bridge::run_host() {
        eprintln!("relay bridge stopped: {error}");
    }
}
