use relay_activity_bridge::{now_iso, validate_client_json, ClientMessage, MAX_MESSAGE_BYTES};
use serde_json::{json, Value};
use std::env;
use std::fs;
use std::io::{self, Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::path::PathBuf;
use std::time::Duration;

fn main() {
    let stdin = io::stdin();
    let mut input = stdin.lock();
    let mut output = io::stdout().lock();
    loop {
        match read_native(&mut input) {
            Ok(bytes) => {
                let response = handle(&bytes);
                if write_native(&mut output, &response).is_err() {
                    break;
                }
            }
            Err(_) => break,
        }
    }
}

fn handle(bytes: &[u8]) -> Value {
    match validate_client_json(bytes) {
        Ok(message) => forward(message),
        Err(reason) => json!({ "ok": false, "error": reason, "at": now_iso() }),
    }
}

fn forward(message: ClientMessage) -> Value {
    let Some(bridge) = read_bridge() else {
        return json!({ "ok": false, "error": "relay_unavailable" });
    };
    let body = match message {
        ClientMessage::Hello => json!({ "type": "chrome.hello" }),
        ClientMessage::Control(false) => json!({ "type": "observation.pause" }),
        ClientMessage::Control(true) => json!({ "type": "observation.resume" }),
        ClientMessage::Observation(value) => value,
    };
    let request = json!({ "token": bridge.token, "body": body });
    let Ok(addr) = format!("127.0.0.1:{}", bridge.port).parse::<SocketAddr>() else {
        return json!({ "ok": false, "error": "relay_unavailable" });
    };
    let mut stream = match TcpStream::connect_timeout(&addr, Duration::from_secs(2)) {
        Ok(stream) => stream,
        Err(_) => return json!({ "ok": false, "error": "relay_unavailable" }),
    };
    let _ = stream.set_read_timeout(Some(Duration::from_secs(2)));
    let _ = stream.set_write_timeout(Some(Duration::from_secs(2)));
    let line = format!("{request}\n");
    if stream.write_all(line.as_bytes()).is_err() {
        return json!({ "ok": false, "error": "relay_unavailable" });
    }
    let mut response = String::new();
    let mut buf = [0u8; 1024];
    loop {
        match stream.read(&mut buf) {
            Ok(0) => break,
            Ok(count) => {
                response.push_str(&String::from_utf8_lossy(&buf[..count]));
                if response.contains('\n') {
                    break;
                }
                if response.len() > MAX_MESSAGE_BYTES {
                    return json!({ "ok": false, "error": "oversized" });
                }
            }
            Err(_) => break,
        }
    }
    serde_json::from_str::<Value>(response.lines().next().unwrap_or(""))
        .unwrap_or_else(|_| json!({ "ok": false, "error": "relay_unavailable" }))
}

struct BridgeFile {
    port: u16,
    token: String,
}

fn read_bridge() -> Option<BridgeFile> {
    let path = bridge_path()?;
    let text = fs::read_to_string(path).ok()?;
    let value: Value = serde_json::from_str(&text).ok()?;
    let port = value.get("port")?.as_u64()? as u16;
    let token = value.get("token")?.as_str()?.to_string();
    if token.len() < 16 {
        return None;
    }
    Some(BridgeFile { port, token })
}

fn bridge_path() -> Option<PathBuf> {
    if let Some(path) = env::var_os("RELAY_BRIDGE_FILE") {
        return Some(PathBuf::from(path));
    }
    if let Ok(local) = env::var("LOCALAPPDATA") {
        return Some(PathBuf::from(local).join("RELAY").join("chrome-bridge.json"));
    }
    let home = env::var_os("HOME")?;
    Some(PathBuf::from(home).join(".local").join("share").join("RELAY").join("chrome-bridge.json"))
}

fn read_native(input: &mut impl Read) -> io::Result<Vec<u8>> {
    let mut len_buf = [0u8; 4];
    input.read_exact(&mut len_buf)?;
    let len = u32::from_le_bytes(len_buf) as usize;
    if len > MAX_MESSAGE_BYTES {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "oversized"));
    }
    let mut buf = vec![0u8; len];
    input.read_exact(&mut buf)?;
    Ok(buf)
}

fn write_native(output: &mut impl Write, value: &Value) -> io::Result<()> {
    let bytes = serde_json::to_vec(value).unwrap_or_else(|_| b"{\"ok\":false}".to_vec());
    let len = u32::try_from(bytes.len()).unwrap_or(0).to_le_bytes();
    output.write_all(&len)?;
    output.write_all(&bytes)?;
    output.flush()
}
