use serde::Deserialize;
use std::fs;
use std::io::Write;
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{LocalFree, HLOCAL};
use windows::Win32::Security::Cryptography::{
    CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
};

const SECRET_NAME: &str = "typesafe_api_key";

#[derive(Deserialize)]
pub struct SecretSetRequest {
    pub name: String,
    pub value: String,
}

#[derive(Deserialize)]
pub struct SecretNameRequest {
    pub name: String,
}

fn secrets_dir() -> Result<PathBuf, String> {
    let base = std::env::var("LOCALAPPDATA").map_err(|_| "LOCALAPPDATA missing".to_string())?;
    let dir = PathBuf::from(base).join("RELAY").join("secrets");
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir)
}

fn secret_path(name: &str) -> Result<PathBuf, String> {
    if name != SECRET_NAME {
        return Err("secret_name_unsupported".into());
    }
    Ok(secrets_dir()?.join(format!("{name}.dpapi")))
}

fn protect(bytes: &[u8]) -> Result<Vec<u8>, String> {
    let input = CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };
    unsafe {
        CryptProtectData(
            &input,
            PCWSTR::null(),
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
        .map_err(|e| e.to_string())?;
        if output.pbData.is_null() || output.cbData == 0 {
            return Err("dpapi_protect_empty".into());
        }
        let protected = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        let _ = LocalFree(HLOCAL(output.pbData as *mut _));
        Ok(protected)
    }
}

fn unprotect(bytes: &[u8]) -> Result<Vec<u8>, String> {
    let input = CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };
    unsafe {
        CryptUnprotectData(
            &input,
            None,
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
        .map_err(|e| e.to_string())?;
        if output.pbData.is_null() || output.cbData == 0 {
            return Err("dpapi_unprotect_empty".into());
        }
        let plain = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        let _ = LocalFree(HLOCAL(output.pbData as *mut _));
        Ok(plain)
    }
}

pub fn read_typesafe_api_key() -> Result<String, String> {
    let path = secret_path(SECRET_NAME)?;
    if !path.exists() {
        return Err("missing_secret".into());
    }
    let protected = fs::read(&path).map_err(|e| e.to_string())?;
    let plain = unprotect(&protected)?;
    let value = String::from_utf8(plain).map_err(|_| "secret_utf8".to_string())?;
    if value.trim().is_empty() {
        return Err("missing_secret".into());
    }
    Ok(value)
}

#[tauri::command]
pub fn secret_status() -> String {
    match read_typesafe_api_key() {
        Ok(_) => "present".into(),
        Err(_) => "disabled".into(),
    }
}

pub fn secret_set(request: SecretSetRequest) -> Result<(), String> {
    if request.name != SECRET_NAME {
        return Err("secret_name_unsupported".into());
    }
    if request.value.trim().is_empty() {
        return Err("secret_empty".into());
    }
    persist_protected(request.value.as_bytes())
}

fn persist_protected(plain: &[u8]) -> Result<(), String> {
    let path = secret_path(SECRET_NAME)?;
    let protected = protect(plain)?;
    let parent = path.parent().ok_or_else(|| "secret_parent".to_string())?;
    fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    let tmp = parent.join(format!(".{SECRET_NAME}.tmp"));
    {
        let mut file = fs::File::create(&tmp).map_err(|e| e.to_string())?;
        file.write_all(&protected).map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
    }
    fs::rename(&tmp, &path).map_err(|e| e.to_string())?;
    Ok(())
}

const MAX_STAGING_BYTES: u64 = 4096;

/// Read a one-line staging file in-process. The plaintext never leaves this function's caller.
pub fn import_staging_file(path: &Path) -> Result<(), String> {
    let key = read_staging_key(path)?;
    persist_protected(key.as_bytes())?;
    let confirmed = read_typesafe_api_key()?;
    if confirmed != key {
        return Err("secret_confirm_mismatch".into());
    }
    fs::remove_file(path).map_err(|_| "secret_staging_delete".to_string())?;
    Ok(())
}

fn read_staging_key(path: &Path) -> Result<String, String> {
    let meta = fs::symlink_metadata(path).map_err(|e| e.to_string())?;
    if meta.is_dir() {
        return Err("secret_directory".into());
    }
    if meta.file_attributes() & 0x400 != 0 {
        return Err("secret_reparse".into());
    }
    if !meta.is_file() {
        return Err("secret_not_file".into());
    }
    if meta.len() > MAX_STAGING_BYTES {
        return Err("secret_oversize".into());
    }
    if inside_relay_checkout(path) {
        return Err("secret_in_repository".into());
    }
    let bytes = fs::read(path).map_err(|e| e.to_string())?;
    parse_key_file(&bytes)
}

fn parse_key_file(bytes: &[u8]) -> Result<String, String> {
    if bytes.len() > MAX_STAGING_BYTES as usize {
        return Err("secret_oversize".into());
    }
    let text = std::str::from_utf8(bytes).map_err(|_| "secret_format".to_string())?;
    let line = text
        .strip_suffix("\r\n")
        .or_else(|| text.strip_suffix('\n'))
        .unwrap_or(text);
    if line.contains('\n') || line.contains('\r') {
        return Err("secret_multiline".into());
    }
    if line.is_empty() || line != line.trim() {
        return Err("secret_format".into());
    }
    if line.len() < 16 || line.len() > 512 {
        return Err("secret_format".into());
    }
    if !line
        .chars()
        .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.' | '~' | '+' | '/'))
    {
        return Err("secret_format".into());
    }
    Ok(line.to_string())
}

fn inside_relay_checkout(path: &Path) -> bool {
    let mut cursor = path.parent().map(Path::to_path_buf);
    while let Some(dir) = cursor {
        let marker = dir.join("apps").join("desktop").join("src-tauri").join("Cargo.toml");
        if dir.join(".git").exists() && marker.exists() {
            return true;
        }
        cursor = dir.parent().map(Path::to_path_buf);
    }
    false
}

#[tauri::command]
pub fn secret_import_staging_file() -> Result<(), String> {
    let path = pick_staging_file()?;
    import_staging_file(&path)
}

fn pick_staging_file() -> Result<PathBuf, String> {
    use std::ffi::c_void;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{HWND, LPARAM};
    use windows::Win32::UI::Controls::Dialogs::{
        GetOpenFileNameW, OFN_FILEMUSTEXIST, OFN_NOCHANGEDIR, OFN_PATHMUSTEXIST, OPENFILENAMEW,
    };
    let mut file = [0u16; 512];
    let filter: Vec<u16> = "Text files\0*.txt\0\0".encode_utf16().collect();
    let title: Vec<u16> = "Import RELAY key file\0".encode_utf16().collect();
    let mut ofn = OPENFILENAMEW {
        lStructSize: std::mem::size_of::<OPENFILENAMEW>() as u32,
        hwndOwner: HWND::default(),
        hInstance: windows::Win32::Foundation::HINSTANCE::default(),
        lpstrFilter: PCWSTR(filter.as_ptr()),
        lpstrCustomFilter: windows::core::PWSTR::null(),
        nMaxCustFilter: 0,
        nFilterIndex: 1,
        lpstrFile: windows::core::PWSTR(file.as_mut_ptr()),
        nMaxFile: file.len() as u32,
        lpstrFileTitle: windows::core::PWSTR::null(),
        nMaxFileTitle: 0,
        lpstrInitialDir: PCWSTR::null(),
        lpstrTitle: PCWSTR(title.as_ptr()),
        Flags: OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
        nFileOffset: 0,
        nFileExtension: 0,
        lpstrDefExt: PCWSTR::null(),
        lCustData: LPARAM(0),
        lpfnHook: None,
        lpTemplateName: PCWSTR::null(),
        pvReserved: std::ptr::null_mut::<c_void>(),
        dwReserved: 0,
        FlagsEx: Default::default(),
    };
    let chosen = unsafe { GetOpenFileNameW(&mut ofn) };
    if !chosen.as_bool() {
        return Err("secret_picker_cancelled".into());
    }
    let len = file.iter().position(|c| *c == 0).unwrap_or(file.len());
    Ok(PathBuf::from(String::from_utf16_lossy(&file[..len])))
}

#[tauri::command]
pub fn secret_delete(request: SecretNameRequest) -> Result<(), String> {
    let path = secret_path(&request.name)?;
    if path.exists() {
        fs::remove_file(&path).map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// Dev/test bootstrap only: seed DPAPI store from env if present and store empty.
pub fn maybe_seed_from_env() {
    if read_typesafe_api_key().is_ok() {
        return;
    }
    if let Ok(value) = std::env::var("RELAY_TYPESAFE_API_KEY") {
        if value.trim().is_empty() {
            return;
        }
        let _ = secret_set(SecretSetRequest {
            name: SECRET_NAME.into(),
            value,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_env::with_temp_localappdata;

    #[test]
    fn secret_lifecycle_present_and_deleted() {
        with_temp_localappdata("secret-test", |_| {
            assert_eq!(secret_status(), "disabled");
            assert!(read_typesafe_api_key().is_err());

            secret_set(SecretSetRequest {
                name: SECRET_NAME.into(),
                value: "test-key-value".into(),
            })
            .expect("set");
            assert_eq!(secret_status(), "present");
            assert_eq!(read_typesafe_api_key().unwrap(), "test-key-value");

            secret_delete(SecretNameRequest {
                name: SECRET_NAME.into(),
            })
            .expect("delete");
            assert_eq!(secret_status(), "disabled");
            assert!(read_typesafe_api_key().is_err());
        });
    }

    fn write_staging(dir: &std::path::Path, name: &str, body: &str) -> std::path::PathBuf {
        let path = dir.join(name);
        fs::write(&path, body).expect("staging");
        path
    }

    #[test]
    fn import_replaces_rereads_and_deletes_staging_file() {
        with_temp_localappdata("secret-import", |dir| {
            let staging = write_staging(dir, "relay-jev-key.txt", "test-key-value-aaaa\n");
            import_staging_file(&staging).expect("import");
            assert!(!staging.exists());
            assert_eq!(read_typesafe_api_key().unwrap(), "test-key-value-aaaa");

            let replacement = write_staging(dir, "relay-jev-key-2.txt", "test-key-value-bbbb");
            import_staging_file(&replacement).expect("replace");
            assert!(!replacement.exists());
            assert_eq!(read_typesafe_api_key().unwrap(), "test-key-value-bbbb");

            secret_delete(SecretNameRequest {
                name: SECRET_NAME.into(),
            })
            .expect("delete");
            assert!(read_typesafe_api_key().is_err());
        });
    }

    #[test]
    fn import_rejects_malformed_staging_files_and_keeps_them() {
        with_temp_localappdata("secret-malformed", |dir| {
            let empty = write_staging(dir, "empty.txt", "\n");
            assert_eq!(import_staging_file(&empty).unwrap_err(), "secret_format");
            assert!(empty.exists());

            let lines = write_staging(dir, "lines.txt", "test-key-value-aaaa\nsecond-line-value\n");
            assert_eq!(import_staging_file(&lines).unwrap_err(), "secret_multiline");
            assert!(lines.exists());

            let short = write_staging(dir, "short.txt", "too-short\n");
            assert_eq!(import_staging_file(&short).unwrap_err(), "secret_format");

            let spaced = write_staging(dir, "spaced.txt", "test-key-value-aaaa \n");
            assert_eq!(import_staging_file(&spaced).unwrap_err(), "secret_format");

            fs::create_dir(dir.join("not-a-file")).unwrap();
            assert_eq!(
                import_staging_file(&dir.join("not-a-file")).unwrap_err(),
                "secret_directory"
            );

            let huge = dir.join("huge.txt");
            fs::write(&huge, "a".repeat(5000)).unwrap();
            assert_eq!(import_staging_file(&huge).unwrap_err(), "secret_oversize");
            assert!(huge.exists());
            assert!(read_typesafe_api_key().is_err());
        });
    }

    #[test]
    fn import_rejects_a_file_inside_the_relay_checkout() {
        with_temp_localappdata("secret-repo", |dir| {
            let repo = dir.join("checkout");
            fs::create_dir_all(repo.join(".git")).unwrap();
            fs::create_dir_all(repo.join("apps").join("desktop").join("src-tauri")).unwrap();
            fs::write(
                repo.join("apps").join("desktop").join("src-tauri").join("Cargo.toml"),
                "[package]\nname = \"relay-desktop\"\n",
            )
            .unwrap();
            let staging = write_staging(&repo, "key.txt", "test-key-value-aaaa\n");
            assert_eq!(import_staging_file(&staging).unwrap_err(), "secret_in_repository");
            assert!(staging.exists());
            assert!(read_typesafe_api_key().is_err());
        });
    }
}
