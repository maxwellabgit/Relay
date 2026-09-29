use serde_json::{json, Map, Value};
use std::time::{SystemTime, UNIX_EPOCH};

pub const MAX_MESSAGE_BYTES: usize = 256 * 1024;

const ALLOWED_TYPES: &[&str] = &[
    "chrome.tab.activated",
    "chrome.navigation",
    "chrome.page.metadata",
    "chrome.page.classified",
    "chrome.permission.changed",
    "chrome.hello",
];

const FORBIDDEN_KEYS: &[&str] = &[
    "password",
    "passwd",
    "pwd",
    "secret",
    "token",
    "authorization",
    "cookie",
    "cvv",
    "cvc",
    "ssn",
    "creditcard",
    "credit_card",
    "cardnumber",
    "command",
    "tool",
    "invoke",
    "shell",
    "argv",
    "__proto__",
    "constructor",
    "prototype",
];

#[derive(Debug, PartialEq, Eq)]
pub enum ClientMessage {
    Hello,
    Observation(Value),
}

pub fn validate_client_json(bytes: &[u8]) -> Result<ClientMessage, &'static str> {
    if bytes.len() > MAX_MESSAGE_BYTES {
        return Err("oversized");
    }
    let value: Value = serde_json::from_slice(bytes).map_err(|_| "malformed")?;
    let row = value.as_object().ok_or("malformed")?;
    if row.contains_key("command") || row.contains_key("tool") || row.contains_key("invoke") || row.contains_key("shell") {
        return Err("forbidden_field");
    }
    let kind = row.get("type").and_then(|item| item.as_str()).ok_or("type")?;
    if !ALLOWED_TYPES.contains(&kind) {
        return Err("type");
    }
    if kind == "chrome.hello" {
        return Ok(ClientMessage::Hello);
    }
    let observation = row.get("observation").and_then(|item| item.as_object()).ok_or("observation")?;
    let event_type = observation.get("eventType").and_then(|item| item.as_str()).ok_or("event_type")?;
    if event_type != kind {
        return Err("event_type");
    }
    let mut sanitized = sanitize_object(observation, 0);
    if let Some(object) = sanitized.as_object_mut() {
        object.insert(
            "source".into(),
            json!({ "type": "chrome", "provider": "extension" }),
        );
    }
    Ok(ClientMessage::Observation(json!({
        "type": kind,
        "observation": sanitized,
    })))
}

fn forbidden(key: &str) -> bool {
    let lower = key.to_ascii_lowercase();
    let compact: String = lower.chars().filter(|ch| ch.is_ascii_alphanumeric()).collect();
    FORBIDDEN_KEYS.contains(&lower.as_str()) || FORBIDDEN_KEYS.contains(&compact.as_str())
}

fn sanitize_object(input: &Map<String, Value>, depth: usize) -> Value {
    if depth > 5 {
        return Value::Null;
    }
    let mut out = Map::new();
    for (key, value) in input {
        if forbidden(key) {
            continue;
        }
        if let Some(cleaned) = sanitize_value(value, depth + 1) {
            out.insert(key.clone(), cleaned);
        }
    }
    Value::Object(out)
}

fn sanitize_value(value: &Value, depth: usize) -> Option<Value> {
    if depth > 6 {
        return None;
    }
    match value {
        Value::String(text) => Some(Value::String(scrub(text))),
        Value::Number(number) => Some(Value::Number(number.clone())),
        Value::Bool(flag) => Some(Value::Bool(*flag)),
        Value::Array(items) => Some(Value::Array(
            items.iter().take(8).filter_map(|item| sanitize_value(item, depth + 1)).collect(),
        )),
        Value::Object(map) => Some(sanitize_object(map, depth)),
        Value::Null => None,
    }
}

fn scrub(value: &str) -> String {
    let mut out = String::new();
    let lower = value.to_ascii_lowercase();
    if let Some(index) = lower.find("bearer ") {
        out.push_str(&value[..index]);
        out.push_str("Bearer [redacted]");
        let rest = index + "bearer ".len();
        let tail = value[rest..].find(' ').map(|at| rest + at).unwrap_or(value.len());
        out.push_str(&value[tail..]);
    } else {
        out.push_str(value);
    }
    if out.len() > 4_000 {
        out.truncate(4_000);
    }
    out
}

pub fn unix_millis_to_iso(ms: u64) -> String {
    let secs = ms / 1000;
    let millis = ms % 1000;
    let days = secs / 86_400;
    let tod = secs % 86_400;
    let hour = tod / 3_600;
    let minute = (tod % 3_600) / 60;
    let second = tod % 60;
    let z = days + 719_468;
    let era = z / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let mut year = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    if month <= 2 {
        year += 1;
    }
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{millis:03}Z")
}

pub fn now_iso() -> String {
    let ms = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis() as u64).unwrap_or(0);
    unix_millis_to_iso(ms)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn epoch_formats() {
        assert_eq!(unix_millis_to_iso(0), "1970-01-01T00:00:00.000Z");
        assert_eq!(unix_millis_to_iso(86_400_000), "1970-01-02T00:00:00.000Z");
    }

    #[test]
    fn rejects_tool_and_oversized_messages() {
        let tool = br#"{"type":"tool.execute","command":"shell"}"#;
        assert_eq!(validate_client_json(tool).unwrap_err(), "forbidden_field");
        let named = br#"{"type":"tool.execute"}"#;
        assert_eq!(validate_client_json(named).unwrap_err(), "type");
        let huge = vec![b' '; MAX_MESSAGE_BYTES + 1];
        assert_eq!(validate_client_json(&huge).unwrap_err(), "oversized");
        let hello = br#"{"type":"chrome.hello"}"#;
        assert_eq!(validate_client_json(hello).unwrap(), ClientMessage::Hello);
        let page = br#"{"type":"chrome.navigation","observation":{"eventType":"chrome.navigation","data":{"password":"hunter2","text":"Role"}}}"#;
        let ok = validate_client_json(page).unwrap();
        let ClientMessage::Observation(value) = ok else { panic!("observation") };
        let encoded = value.to_string();
        assert!(!encoded.contains("hunter2"));
        assert!(encoded.contains("Role"));
    }
}
