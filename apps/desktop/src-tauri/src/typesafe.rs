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
}

fn fail(category: &str, status: u16, latency_ms: u64, retries: u32) -> TypesafeJudgeResult {
    TypesafeJudgeResult {
        ok: false,
        status,
        category: category.to_string(),
        latency_ms,
        retries,
        body: None,
    }
}

fn category_for_status(status: u16) -> &'static str {
    match status {
        401 => "authentication",
        422 => "validation",
        429 => "rate_limited",
        529 => "overloaded",
        _ => "network",
    }
}

fn post_once(
    agent: &ureq::Agent,
    api_key: &str,
    body: &serde_json::Value,
) -> Result<(u16, Option<serde_json::Value>), (u16, bool)> {
    match agent
        .post("https://api.typesafe.ai/v1/systemone")
        .set("Authorization", &format!("Bearer {api_key}"))
        .set("Content-Type", "application/json")
        .send_json(body)
    {
        Ok(response) => {
            let status = response.status();
            if !(200..300).contains(&status) {
                return Err((status, status == 429 || status == 529));
            }
            match response.into_json::<serde_json::Value>() {
                Ok(parsed) => Ok((status, Some(parsed))),
                Err(_) => Err((status, false)),
            }
        }
        Err(ureq::Error::Status(status, _response)) => {
            Err((status, status == 429 || status == 529))
        }
        Err(ureq::Error::Transport(_)) => Err((0, false)),
    }
}

#[tauri::command]
pub fn typesafe_judge(request: TypesafeJudgeRequest) -> TypesafeJudgeResult {
    let _ = &request.model;
    let started = Instant::now();

    let api_key = match read_typesafe_api_key() {
        Ok(value) => value,
        Err(_) => {
            return fail("missing_secret", 0, started.elapsed().as_millis() as u64, 0);
        }
    };

    let agent = ureq::AgentBuilder::new()
        .timeout(Duration::from_secs(15))
        .build();

    let mut retries: u32 = 0;
    let mut attempt = 0u32;
    loop {
        attempt += 1;
        match post_once(&agent, &api_key, &request.body) {
            Ok((status, body)) => {
                return TypesafeJudgeResult {
                    ok: true,
                    status,
                    category: "ok".to_string(),
                    latency_ms: started.elapsed().as_millis() as u64,
                    retries,
                    body,
                };
            }
            Err((status, retryable)) => {
                if retryable && attempt < 2 {
                    retries = 1;
                    std::thread::sleep(Duration::from_millis(250));
                    continue;
                }
                let category = if status == 0 {
                    "network"
                } else {
                    category_for_status(status)
                };
                return fail(
                    category,
                    status,
                    started.elapsed().as_millis() as u64,
                    retries,
                );
            }
        }
    }
}
