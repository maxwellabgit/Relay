use serde::{Deserialize, Serialize};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

static CANCEL_GENERATION: AtomicU64 = AtomicU64::new(0);

use crate::secrets::read_typesafe_api_key;

#[derive(Deserialize)]
pub struct TypesafeJudgeRequest {
    pub model: String,
    pub body: serde_json::Value,
}

#[derive(Serialize)]
pub struct TypesafeJudgeResult {
    pub ok: bool,
    pub status: u16,
    pub category: String,
    pub latency_ms: u64,
    pub retries: u32,
    pub body: Option<serde_json::Value>,
    pub retry_after: Option<String>,
    pub request_id: Option<String>,
}

fn fail(
    category: &str,
    status: u16,
    latency_ms: u64,
    retry_after: Option<String>,
    request_id: Option<String>,
) -> TypesafeJudgeResult {
    TypesafeJudgeResult {
        ok: false,
        status,
        category: category.to_string(),
        latency_ms,
        retries: 0,
        body: None,
        retry_after,
        request_id,
    }
}

fn category_for_status(status: u16) -> &'static str {
    match status {
        401 => "authentication",
        402 | 422 => "validation",
        429 => "rate_limited",
        529 => "overloaded",
        _ => "network",
    }
}

fn header_value(response: &ureq::Response, name: &str) -> Option<String> {
    response.header(name).map(str::to_string)
}

fn response_meta(response: &ureq::Response) -> (Option<String>, Option<String>) {
    let retry_after = header_value(response, "retry-after");
    let request_id =
        header_value(response, "x-request-id").or_else(|| header_value(response, "request-id"));
    (retry_after, request_id)
}

struct HttpExchange {
    status: u16,
    body: Option<serde_json::Value>,
    retry_after: Option<String>,
    request_id: Option<String>,
}

fn post_once(
    agent: &ureq::Agent,
    api_key: &str,
    body: &serde_json::Value,
) -> Result<HttpExchange, HttpExchange> {
    match agent
        .post("https://api.typesafe.ai/v1/systemone")
        .set("Authorization", &format!("Bearer {api_key}"))
        .set("Content-Type", "application/json")
        .send_json(body)
    {
        Ok(response) => {
            let status = response.status();
            let (retry_after, request_id) = response_meta(&response);
            if !(200..300).contains(&status) {
                return Err(HttpExchange {
                    status,
                    body: None,
                    retry_after,
                    request_id,
                });
            }
            match response.into_json::<serde_json::Value>() {
                Ok(parsed) => Ok(HttpExchange {
                    status,
                    body: Some(parsed),
                    retry_after,
                    request_id,
                }),
                Err(_) => Err(HttpExchange {
                    status,
                    body: None,
                    retry_after,
                    request_id,
                }),
            }
        }
        Err(ureq::Error::Status(status, response)) => {
            let (retry_after, request_id) = response_meta(&response);
            Err(HttpExchange {
                status,
                body: None,
                retry_after,
                request_id,
            })
        }
        Err(ureq::Error::Transport(_)) => Err(HttpExchange {
            status: 0,
            body: None,
            retry_after: None,
            request_id: None,
        }),
    }
}

/// Connectivity evidence only. Grant, disclosure, UI, and health live in the packaged app.
#[cfg(test)]
mod live_canary {
    use super::typesafe_judge;
    use super::TypesafeJudgeRequest;

    #[test]
    fn minimum_kinds_when_requested() {
        if std::env::var("RELAY_LIVE_CANARY").ok().as_deref() != Some("1") {
            return;
        }
        let cases = [
            (
                "noul",
                serde_json::json!({
                    "model": "jev-latest",
                    "state": { "purpose": "connectivity" },
                    "questions": {
                        "useful": { "type": "noul", "instructions": "Is this a connectivity check?" }
                    }
                }),
            ),
            (
                "choice",
                serde_json::json!({
                    "model": "jev-latest",
                    "state": { "purpose": "connectivity" },
                    "questions": {
                        "pick": {
                            "type": "choice",
                            "instructions": "Pick one.",
                            "criteria": { "yes": "Yes", "no": "No" }
                        }
                    }
                }),
            ),
            (
                "score",
                serde_json::json!({
                    "model": "jev-latest",
                    "state": { "purpose": "connectivity" },
                    "questions": {
                        "rank": {
                            "type": "score",
                            "instructions": "Score clarity from 0 to 1.",
                            "criteria": ["clarity"]
                        }
                    }
                }),
            ),
        ];
        let mut rows = Vec::new();
        for (kind, body) in cases {
            let result = typesafe_judge(TypesafeJudgeRequest {
                model: "jev-latest".into(),
                body,
            });
            let answer = result
                .body
                .as_ref()
                .and_then(|value| value.get("answers"))
                .map(|value| value.to_string())
                .unwrap_or_default();
            rows.push(serde_json::json!({
                "evidence": "connectivity",
                "kind": kind,
                "ok": result.ok,
                "status": result.status,
                "category": result.category,
                "latencyMs": result.latency_ms,
                "requestId": result.request_id,
                "answer": answer,
            }));
            assert!(
                result.ok,
                "{kind} canary failed status={} category={}",
                result.status, result.category
            );
        }
        let dir =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../.dev-data/canary");
        std::fs::create_dir_all(&dir).expect("canary dir");
        std::fs::write(
            dir.join("result.json"),
            format!("{}\n", serde_json::json!(rows)),
        )
        .expect("canary result");
    }
}

#[tauri::command]
pub fn typesafe_cancel() {
    CANCEL_GENERATION.fetch_add(1, Ordering::SeqCst);
}

/// One native HTTP attempt. A newer cancel generation returns without waiting
/// for the socket, so an in-flight request does not block the desktop command.
#[tauri::command]
pub fn typesafe_judge(request: TypesafeJudgeRequest) -> TypesafeJudgeResult {
    let _ = &request.model;
    let started = Instant::now();
    let generation = CANCEL_GENERATION.load(Ordering::SeqCst);

    let api_key = match read_typesafe_api_key() {
        Ok(value) => value,
        Err(_) => {
            return fail(
                "missing_secret",
                0,
                started.elapsed().as_millis() as u64,
                None,
                None,
            );
        }
    };

    let body = request.body;
    let (sender, receiver) = mpsc::channel();
    thread::spawn(move || {
        let agent = ureq::AgentBuilder::new()
            .timeout(Duration::from_secs(15))
            .build();
        let _ = sender.send(post_once(&agent, &api_key, &body));
    });

    loop {
        if CANCEL_GENERATION.load(Ordering::SeqCst) != generation {
            return fail(
                "cancelled",
                0,
                started.elapsed().as_millis() as u64,
                None,
                None,
            );
        }
        match receiver.recv_timeout(Duration::from_millis(40)) {
            Ok(Ok(exchange)) => {
                return TypesafeJudgeResult {
                    ok: true,
                    status: exchange.status,
                    category: "ok".to_string(),
                    latency_ms: started.elapsed().as_millis() as u64,
                    retries: 0,
                    body: exchange.body,
                    retry_after: exchange.retry_after,
                    request_id: exchange.request_id,
                };
            }
            Ok(Err(exchange)) => {
                let category = if exchange.status == 0 {
                    "network"
                } else {
                    category_for_status(exchange.status)
                };
                return fail(
                    category,
                    exchange.status,
                    started.elapsed().as_millis() as u64,
                    exchange.retry_after,
                    exchange.request_id,
                );
            }
            Err(mpsc::RecvTimeoutError::Timeout) => continue,
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                return fail(
                    "network",
                    0,
                    started.elapsed().as_millis() as u64,
                    None,
                    None,
                );
            }
        }
    }
}
