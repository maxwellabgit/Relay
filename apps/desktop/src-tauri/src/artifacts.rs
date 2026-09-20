use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{LocalFree, HLOCAL};
use windows::Win32::Security::Cryptography::{
    CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
};

#[derive(Deserialize)]
pub struct ArtifactPutRequest {
    pub bytes_b64: String,
    pub policy: serde_json::Value,
}

#[derive(Serialize)]
pub struct ArtifactPutResult {
    pub artifact_id: String,
    pub sha256: String,
}

#[derive(Deserialize)]
pub struct ArtifactGetRequest {
    pub artifact_id: String,
    pub sha256: String,
}

#[derive(Serialize)]
pub struct ArtifactGetResult {
    pub bytes_b64: String,
}

fn objects_dir() -> Result<PathBuf, String> {
    let base = std::env::var("LOCALAPPDATA").map_err(|_| "LOCALAPPDATA missing".to_string())?;
    let dir = PathBuf::from(base).join("RELAY").join("objects");
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir)
}

fn sha256_hex(bytes: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    hasher
        .finalize()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
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

fn decode_b64(value: &str) -> Result<Vec<u8>, String> {
    use base64::Engine as _;
    base64::engine::general_purpose::STANDARD
        .decode(value)
        .map_err(|e| e.to_string())
}

fn encode_b64(bytes: &[u8]) -> String {
    use base64::Engine as _;
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let parent = path
        .parent()
        .ok_or_else(|| "artifact_parent_missing".to_string())?;
    fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    let tmp = parent.join(format!(
        ".{}.tmp",
        path.file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("artifact")
    ));
    {
        let mut file = fs::File::create(&tmp).map_err(|e| e.to_string())?;
        file.write_all(bytes).map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
    }
    fs::rename(&tmp, path).map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
pub fn artifact_put(request: ArtifactPutRequest) -> Result<ArtifactPutResult, String> {
    let _ = request.policy;
    let plain = decode_b64(&request.bytes_b64)?;
    let digest = sha256_hex(&plain);
    let artifact_id = format!("artifact_{}", &digest[..24.min(digest.len())]);
    let path = objects_dir()?.join(format!("{artifact_id}.bin"));
    if !path.exists() {
        let protected = protect(&plain)?;
        atomic_write(&path, &protected)?;
    }
    Ok(ArtifactPutResult {
        artifact_id,
        sha256: digest,
    })
}

#[tauri::command]
pub fn artifact_get(request: ArtifactGetRequest) -> Result<ArtifactGetResult, String> {
    let path = objects_dir()?.join(format!("{}.bin", request.artifact_id));
    if !path.exists() {
        return Err("artifact_missing".into());
    }
    let protected = fs::read(&path).map_err(|e| e.to_string())?;
    let plain = unprotect(&protected)?;
    let digest = sha256_hex(&plain);
    if digest != request.sha256 {
        return Err("artifact_hash_mismatch".into());
    }
    Ok(ArtifactGetResult {
        bytes_b64: encode_b64(&plain),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    static ENV_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn protect_roundtrip_and_hash() {
        let payload = b"relay-sentinel-artifact-v1";
        let protected = protect(payload).expect("protect");
        assert_ne!(protected, payload);
        let plain = unprotect(&protected).expect("unprotect");
        assert_eq!(plain, payload);
        assert_eq!(sha256_hex(payload).len(), 64);
    }

    #[test]
    fn put_get_restart_content_addressing_and_failures() {
        let _guard = ENV_LOCK.lock().unwrap();
        let dir = std::env::temp_dir().join(format!("relay-artifact-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        std::env::set_var("LOCALAPPDATA", &dir);

        let payload = b"relay-restart-sentinel-v1";
        let put = artifact_put(ArtifactPutRequest {
            bytes_b64: encode_b64(payload),
            policy: serde_json::json!({ "disclosure": "local_only", "sensitivity": 0 }),
        })
        .expect("put");
        let put2 = artifact_put(ArtifactPutRequest {
            bytes_b64: encode_b64(payload),
            policy: serde_json::json!({ "disclosure": "local_only", "sensitivity": 0 }),
        })
        .expect("put2");
        assert_eq!(put.artifact_id, put2.artifact_id);
        assert_eq!(put.sha256, put2.sha256);

        let got = artifact_get(ArtifactGetRequest {
            artifact_id: put.artifact_id.clone(),
            sha256: put.sha256.clone(),
        })
        .expect("get");
        assert_eq!(decode_b64(&got.bytes_b64).unwrap(), payload);

        let missing = artifact_get(ArtifactGetRequest {
            artifact_id: "artifact_missing_id".into(),
            sha256: put.sha256.clone(),
        });
        assert!(match missing {
            Err(msg) => msg.contains("artifact_missing"),
            Ok(_) => false,
        });

        let path = objects_dir()
            .unwrap()
            .join(format!("{}.bin", put.artifact_id));
        fs::write(&path, b"corrupted-bytes").unwrap();
        let bad = artifact_get(ArtifactGetRequest {
            artifact_id: put.artifact_id,
            sha256: put.sha256,
        });
        assert!(bad.is_err());

        let _ = fs::remove_dir_all(&dir);
        std::env::remove_var("LOCALAPPDATA");
    }
}
