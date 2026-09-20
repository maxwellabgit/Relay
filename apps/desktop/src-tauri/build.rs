use std::process::Command;

fn main() {
    let sha = resolve_git_commit();
    println!("cargo:rustc-env=GIT_COMMIT={sha}");
    println!("cargo:rerun-if-env-changed=GIT_COMMIT");
    println!("cargo:rerun-if-env-changed=EXPO_PUBLIC_GIT_SHA");
    println!("cargo:rerun-if-env-changed=GITHUB_SHA");
    tauri_build::build()
}

fn resolve_git_commit() -> String {
    for key in ["GIT_COMMIT", "EXPO_PUBLIC_GIT_SHA", "GITHUB_SHA"] {
        if let Ok(value) = std::env::var(key) {
            let trimmed = value.trim();
            if is_sha(trimmed) {
                return trimmed.to_ascii_lowercase();
            }
        }
    }
    if let Ok(output) = Command::new("git").args(["rev-parse", "HEAD"]).output() {
        if output.status.success() {
            let trimmed = String::from_utf8_lossy(&output.stdout);
            let sha = trimmed.trim();
            if is_sha(sha) {
                return sha.to_ascii_lowercase();
            }
        }
    }
    "unknown".to_string()
}

fn is_sha(value: &str) -> bool {
    let len = value.len();
    (7..=40).contains(&len) && value.bytes().all(|b| b.is_ascii_hexdigit())
}
