use serde::{Deserialize, Serialize};
use std::time::{Duration, Instant};

const DEFAULT_PORT: u16 = 8080;
const DEFAULT_TIMEOUT_MS: u64 = 45_000;

fn model_port() -> u16 {
    std::env::var("RELAY_LOCAL_MODEL_PORT")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(DEFAULT_PORT)
}

fn base_url() -> String {
    format!("http://127.0.0.1:{}", model_port())
}

#[derive(Serialize)]
pub struct LocalModelStatus {
    pub ok: bool,
    pub detail: String,
    pub model: Option<String>,
}

#[derive(Deserialize)]
pub struct LocalModelGenerateRequest {
    pub task_kind: String,
    pub prompt_version: Option<String>,
    pub prompt: String,
    pub max_tokens: Option<u32>,
    pub temperature: Option<f32>,
}

#[derive(Serialize)]
#[serde(untagged)]
pub enum LocalModelGenerateResult {
    Ok {
        ok: bool,
        text: String,
        model: String,
        elapsed_ms: u64,
    },
    Err {
        ok: bool,
        failure_reason: String,
        elapsed_ms: u64,
    },
}

fn fail(reason: &str, elapsed_ms: u64) -> LocalModelGenerateResult {
    LocalModelGenerateResult::Err {
        ok: false,
        failure_reason: reason.to_string(),
        elapsed_ms,
    }
}

#[tauri::command]
pub fn local_model_status() -> LocalModelStatus {
    let url = format!("{}/v1/models", base_url());
    let agent = ureq::AgentBuilder::new()
        .timeout(Duration::from_secs(3))
        .build();
    match agent.get(&url).call() {
        Ok(response) => {
            if !(200..300).contains(&response.status()) {
                return LocalModelStatus {
                    ok: false,
                    detail: "unavailable".into(),
                    model: None,
                };
            }
            let model = response
                .into_json::<serde_json::Value>()
                .ok()
                .and_then(|body| {
                    body.get("data")
                        .and_then(|d| d.as_array())
                        .and_then(|arr| arr.first())
                        .and_then(|row| row.get("id"))
                        .and_then(|id| id.as_str())
                        .map(|s| s.to_string())
                });
            LocalModelStatus {
                ok: true,
                detail: "ready".into(),
                model,
            }
        }
        Err(_) => LocalModelStatus {
            ok: false,
            detail: "unavailable".into(),
            model: None,
        },
    }
}

#[tauri::command]
pub fn local_model_generate(request: LocalModelGenerateRequest) -> LocalModelGenerateResult {
    let _ = (&request.task_kind, &request.prompt_version);
    let started = Instant::now();
    if request.prompt.trim().is_empty() {
        return fail("invalid_response", started.elapsed().as_millis() as u64);
    }

    let url = format!("{}/v1/chat/completions", base_url());
    let body = serde_json::json!({
        "model": "local",
        "messages": [
            { "role": "user", "content": request.prompt }
        ],
        "max_tokens": request.max_tokens.unwrap_or(512),
        "temperature": request.temperature.unwrap_or(0.2),
        "stream": false
    });

    let agent = ureq::AgentBuilder::new()
        .timeout(Duration::from_millis(DEFAULT_TIMEOUT_MS))
        .build();

    match agent.post(&url).send_json(body) {
        Ok(response) => {
            let status = response.status();
            if !(200..300).contains(&status) {
                return fail("model_unavailable", started.elapsed().as_millis() as u64);
            }
            match response.into_json::<serde_json::Value>() {
                Ok(parsed) => {
                    let text = parsed
                        .pointer("/choices/0/message/content")
                        .and_then(|v| v.as_str())
                        .map(|s| s.trim().to_string())
                        .filter(|s| !s.is_empty());
                    let model = parsed
                        .get("model")
                        .and_then(|v| v.as_str())
                        .unwrap_or("local")
                        .to_string();
                    match text {
                        Some(text) => LocalModelGenerateResult::Ok {
                            ok: true,
                            text,
                            model,
                            elapsed_ms: started.elapsed().as_millis() as u64,
                        },
                        None => fail("invalid_response", started.elapsed().as_millis() as u64),
                    }
                }
                Err(_) => fail("invalid_response", started.elapsed().as_millis() as u64),
            }
        }
        Err(ureq::Error::Status(_, _)) => {
            fail("model_unavailable", started.elapsed().as_millis() as u64)
        }
        Err(ureq::Error::Transport(transport)) => {
            let reason = if transport
                .to_string()
                .to_ascii_lowercase()
                .contains("timed out")
            {
                "timeout"
            } else {
                "model_unavailable"
            };
            fail(reason, started.elapsed().as_millis() as u64)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn status_reports_unavailable_when_server_absent() {
        std::env::set_var("RELAY_LOCAL_MODEL_PORT", "59999");
        let status = local_model_status();
        assert!(!status.ok);
        assert_eq!(status.detail, "unavailable");
        std::env::remove_var("RELAY_LOCAL_MODEL_PORT");
    }

    #[test]
    fn generate_fails_closed_when_server_absent() {
        std::env::set_var("RELAY_LOCAL_MODEL_PORT", "59999");
        let result = local_model_generate(LocalModelGenerateRequest {
            task_kind: "direct_answer".into(),
            prompt_version: Some("direct-answer.v1".into()),
            prompt: "What is TCP?".into(),
            max_tokens: Some(32),
            temperature: Some(0.2),
        });
        match result {
            LocalModelGenerateResult::Err { failure_reason, .. } => {
                assert!(
                    failure_reason == "model_unavailable" || failure_reason == "timeout",
                    "{failure_reason}"
                );
            }
            LocalModelGenerateResult::Ok { .. } => panic!("expected failure"),
        }
        std::env::remove_var("RELAY_LOCAL_MODEL_PORT");
    }
}
