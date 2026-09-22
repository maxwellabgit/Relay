use serde::{Deserialize, Serialize};
use std::time::{Duration, Instant};

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
    let request_id = header_value(response, "x-request-id")
        .or_else(|| header_value(response, "request-id"));
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

#[tauri::command]
pub fn typesafe_judge(request: TypesafeJudgeRequest) -> TypesafeJudgeResult {
    let _ = &request.model;
    let started = Instant::now();

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

    let agent = ureq::AgentBuilder::new()
        .timeout(Duration::from_secs(15))
        .build();

    match post_once(&agent, &api_key, &request.body) {
        Ok(exchange) => TypesafeJudgeResult {
            ok: true,
            status: exchange.status,
            category: "ok".to_string(),
            latency_ms: started.elapsed().as_millis() as u64,
            retries: 0,
            body: exchange.body,
            retry_after: exchange.retry_after,
            request_id: exchange.request_id,
        },
        Err(exchange) => {
            let category = if exchange.status == 0 {
                "network"
            } else {
                category_for_status(exchange.status)
            };
            fail(
                category,
                exchange.status,
                started.elapsed().as_millis() as u64,
                exchange.retry_after,
                exchange.request_id,
            )
        }
    }
}
