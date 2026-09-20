use serde::Deserialize;
use std::fs;
use std::io::Write;
use std::path::PathBuf;
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

#[tauri::command]
pub fn secret_set(request: SecretSetRequest) -> Result<(), String> {
    if request.name != SECRET_NAME {
        return Err("secret_name_unsupported".into());
    }
    if request.value.trim().is_empty() {
        return Err("secret_empty".into());
    }
    let path = secret_path(&request.name)?;
    let protected = protect(request.value.as_bytes())?;
    let parent = path.parent().ok_or_else(|| "secret_parent".to_string())?;
    fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    let tmp = parent.join(format!(".{}.tmp", request.name));
    {
        let mut file = fs::File::create(&tmp).map_err(|e| e.to_string())?;
        file.write_all(&protected).map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
    }
    fs::rename(&tmp, &path).map_err(|e| e.to_string())?;
    Ok(())
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
    use std::sync::Mutex;

    static ENV_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn secret_lifecycle_present_and_deleted() {
        let _guard = ENV_LOCK.lock().unwrap();
        let dir = std::env::temp_dir().join(format!("relay-secret-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        std::env::set_var("LOCALAPPDATA", &dir);

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

        let _ = fs::remove_dir_all(&dir);
        std::env::remove_var("LOCALAPPDATA");
    }
}
