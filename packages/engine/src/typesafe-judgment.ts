import type {
  JudgmentAnswer,
  JudgmentPort,
  JudgmentQuestion,
  JudgmentRequest,
  JudgmentResponse,
} from "@relay/contracts";
import { validateAnswerProbability } from "@relay/contracts";

export const TYPESAFE_ENDPOINT = "https://api.typesafe.ai/v1/systemone";
export const TYPESAFE_MODEL = "jev-1.13.0";

export type TypeSafeJudgmentOptions = {
  readonly getApiKey: () => string | null | Promise<string | null>;
  readonly fetchImpl?: typeof fetch;
  readonly endpoint?: string;
  readonly model?: string;
  readonly maxAttempts?: number;
  readonly retryDelayMs?: number;
};

export function createTypeSafeJudgmentPort(options: TypeSafeJudgmentOptions): JudgmentPort {
  const fetchImpl = options.fetchImpl ?? fetch;
  const endpoint = options.endpoint ?? TYPESAFE_ENDPOINT;
  const fallbackModel = options.model ?? TYPESAFE_MODEL;
  const maxAttempts = Math.min(5, Math.max(1, options.maxAttempts ?? 2));
  const retryDelayMs = options.retryDelayMs ?? 250;

  return {
    async judge(request, signal): Promise<JudgmentResponse> {
      const key = (await options.getApiKey())?.trim() ?? "";
      if (!key) {
        return {
          ok: false,
          failure: { category: "missing_secret", message: "typesafe_key_missing" },
        };
      }

      const body = JSON.stringify(wireBody(request, fallbackModel));
      let lastNetwork = "TypeSafe connection failed before a response.";

      for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
        if (signal.aborted) {
          return { ok: false, failure: { category: "cancelled", message: "jev_cancelled" } };
        }
        const started = Date.now();
        try {
          const response = await fetchImpl(endpoint, {
            method: "POST",
            headers: {
              Authorization: `Bearer ${key}`,
              "Content-Type": "application/json",
            },
            body,
            signal,
          });
          const text = await response.text();
          const status = response.status;

          if (status === 429 || status === 529) {
            if (attempt >= maxAttempts - 1) {
              return {
                ok: false,
                failure: {
                  category: status === 429 ? "rate_limited" : "overloaded",
                  message: status === 429 ? "typesafe_rate_limited" : "typesafe_overloaded",
                  httpStatus: status,
                },
              };
            }
            await delay(retryDelayMs, signal);
            continue;
          }
          if (status === 401) {
            return {
              ok: false,
              failure: {
                category: "authentication",
                message: "typesafe_rejected_key",
                httpStatus: 401,
              },
            };
          }
          if (status === 422) {
            return {
              ok: false,
              failure: { category: "validation", message: "typesafe_invalid_request", httpStatus: 422 },
            };
          }
          if (status < 200 || status >= 300) {
            return {
              ok: false,
              failure: {
                category: "network",
                message: `typesafe_http_${status}`,
                httpStatus: status,
              },
            };
          }

          return parseTypeSafeBody(text, Date.now() - started);
        } catch (error) {
          if (signal.aborted) {
            return { ok: false, failure: { category: "cancelled", message: "jev_cancelled" } };
          }
          lastNetwork = error instanceof Error ? "typesafe_connection_failed" : lastNetwork;
          if (attempt >= maxAttempts - 1) break;
          await delay(retryDelayMs, signal);
        }
      }

      return { ok: false, failure: { category: "network", message: lastNetwork } };
    },
  };
}

export function parseTypeSafeBody(text: string, elapsedMs: number): JudgmentResponse {
  let root: unknown;
  try {
    root = JSON.parse(text) as unknown;
  } catch {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_invalid_json" } };
  }
  if (!root || typeof root !== "object") {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_invalid_json" } };
  }
  const record = root as Record<string, unknown>;
  if (typeof record.model !== "string" || !record.model.trim()) {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_missing_model" } };
  }
  if (!record.answers || typeof record.answers !== "object") {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_missing_answers" } };
  }

  const answers: Record<string, JudgmentAnswer> = {};
  for (const [id, value] of Object.entries(record.answers as Record<string, unknown>)) {
    const parsed = parseAnswer(id, value);
    if (!parsed.ok) return parsed.response;
    answers[id] = parsed.answer;
  }
  if (Object.keys(answers).length === 0) {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_empty_answers" } };
  }

  const usage =
    record.usage && typeof record.usage === "object"
      ? (record.usage as Record<string, unknown>)
      : {};
  const providerRequestId =
    typeof record.id === "string"
      ? record.id
      : typeof record.request_id === "string"
        ? record.request_id
        : undefined;

  return {
    ok: true,
    success: {
      model: record.model,
      answers,
      inputTokens: numberOrZero(usage.input_tokens),
      outputTokens: numberOrZero(usage.output_tokens),
      elapsedMs: Math.max(0, elapsedMs),
      ...(providerRequestId ? { providerRequestId } : {}),
    },
  };
}

function wireBody(request: JudgmentRequest, fallbackModel: string): Record<string, unknown> {
  const questions: Record<string, unknown> = {};
  for (const [id, question] of Object.entries(request.questions)) {
    questions[id] = wireQuestion(question);
  }
  return {
    state: request.state,
    model: request.model.trim() || fallbackModel,
    questions,
  };
}

function wireQuestion(question: JudgmentQuestion): Record<string, unknown> {
  if (question.type === "noul") {
    return {
      type: "noul",
      instructions: question.instructions,
      ...(question.criteria ? { criteria: question.criteria } : {}),
    };
  }
  if (question.type === "choice") {
    return {
      type: "choice",
      instructions: question.instructions,
      criteria: question.criteria,
    };
  }
  return {
    type: "score",
    instructions: question.instructions,
    criteria: question.criteria,
  };
}

function parseAnswer(
  id: string,
  value: unknown,
): { ok: true; answer: JudgmentAnswer } | { ok: false; response: JudgmentResponse } {
  if (!value || typeof value !== "object") {
    return fail(`typesafe_bad_answer_${id}`);
  }
  const row = value as Record<string, unknown>;
  if (row.type === "noul") {
    const probabilityYes = typeof row.noul === "number" ? row.noul : Number.NaN;
    if (!validateAnswerProbability(probabilityYes)) return fail(`typesafe_bad_noul_${id}`);
    return { ok: true, answer: { type: "noul", probabilityYes } };
  }
  if (row.type === "choice") {
    if (typeof row.choice !== "string" || typeof row.confidence !== "number") {
      return fail(`typesafe_bad_choice_${id}`);
    }
    const probabilities = readProbabilities(row.probabilities);
    if (!probabilities || !validateAnswerProbability(row.confidence)) {
      return fail(`typesafe_bad_choice_${id}`);
    }
    return {
      ok: true,
      answer: {
        type: "choice",
        choice: row.choice,
        probabilities,
        confidence: row.confidence,
      },
    };
  }
  if (row.type === "score") {
    if (
      typeof row.score !== "number" ||
      typeof row.confidence !== "number" ||
      !row.legend ||
      typeof row.legend !== "object"
    ) {
      return fail(`typesafe_bad_score_${id}`);
    }
    const probabilities = readProbabilities(row.probabilities);
    if (!probabilities || !validateAnswerProbability(row.confidence)) {
      return fail(`typesafe_bad_score_${id}`);
    }
    const legend: Record<string, string> = {};
    for (const [key, label] of Object.entries(row.legend as Record<string, unknown>)) {
      legend[key] = typeof label === "string" ? label : "";
    }
    return {
      ok: true,
      answer: {
        type: "score",
        score: row.score,
        legend,
        probabilities,
        confidence: row.confidence,
      },
    };
  }
  return fail(`typesafe_bad_answer_${id}`);
}

function readProbabilities(value: unknown): Record<string, number> | null {
  if (!value || typeof value !== "object") return null;
  const out: Record<string, number> = {};
  for (const [key, raw] of Object.entries(value as Record<string, unknown>)) {
    if (typeof raw !== "number" || !validateAnswerProbability(raw)) return null;
    out[key] = raw;
  }
  return out;
}

function fail(message: string): { ok: false; response: JudgmentResponse } {
  return {
    ok: false,
    response: { ok: false, failure: { category: "invalid_response", message } },
  };
}

function numberOrZero(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function delay(ms: number, signal: AbortSignal): Promise<void> {
  if (ms <= 0) return Promise.resolve();
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        resolve();
      },
      { once: true },
    );
  });
}
