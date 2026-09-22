import type {
  JudgmentAnswer,
  JudgmentFailure,
  JudgmentPort,
  JudgmentQuestion,
  JudgmentRequest,
  JudgmentResponse,
} from "@relay/contracts";
import { validateAnswerProbability } from "@relay/contracts";

export const TYPESAFE_ENDPOINT = "https://api.typesafe.ai/v1/systemone";
/** Single production Jev model. Numeric `jev-x.y.z` values are explicit pins only. */
export const JEV_MODEL = "jev-latest";
export const TYPESAFE_MODEL = JEV_MODEL;

const RETRY_BASE_MS = 250;
const RETRY_CAP_MS = 8_000;
const PROBABILITY_SUM_TOLERANCE = 0.02;

export type TypeSafeJudgmentOptions = {
  readonly getApiKey: () => string | null | Promise<string | null>;
  readonly fetchImpl?: typeof fetch;
  readonly endpoint?: string;
  readonly model?: string;
  readonly maxAttempts?: number;
  readonly retryDelayMs?: number;
  readonly random?: () => number;
  readonly now?: () => number;
  readonly sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
};

export type TypeSafeAttempt =
  | {
      readonly kind: "http";
      readonly status: number;
      readonly bodyText: string;
      readonly retryAfter: string | null;
      readonly requestId: string | null;
      readonly elapsedMs: number;
    }
  | {
      readonly kind: "transport";
      readonly reason: "timeout" | "network" | "cancelled";
    }
  | {
      readonly kind: "terminal";
      readonly failure: JudgmentFailure;
    };

export function resolveProviderModel(requested: string | undefined): string {
  const trimmed = requested?.trim() ?? "";
  if (trimmed === JEV_MODEL) return JEV_MODEL;
  if (/^jev-\d+\.\d+\.\d+$/.test(trimmed)) return trimmed;
  return JEV_MODEL;
}

export function wireTypeSafeBody(
  request: Pick<JudgmentRequest, "state" | "model" | "questions">,
): { state: unknown; model: string; questions: Record<string, unknown> } {
  const questions: Record<string, unknown> = {};
  for (const [id, question] of Object.entries(request.questions)) {
    questions[id] = wireQuestion(question);
  }
  return {
    state: request.state,
    model: resolveProviderModel(request.model),
    questions,
  };
}

export function isRetryableHttpStatus(status: number): boolean {
  return status === 0 || status === 429 || status === 529;
}

export function failureForHttpStatus(status: number): JudgmentFailure {
  if (status === 401) {
    return { category: "authentication", message: "typesafe_rejected_key", httpStatus: 401 };
  }
  if (status === 402) {
    return { category: "validation", message: "typesafe_payment_required", httpStatus: 402 };
  }
  if (status === 422) {
    return { category: "validation", message: "typesafe_invalid_request", httpStatus: 422 };
  }
  if (status === 429) {
    return { category: "rate_limited", message: "typesafe_rate_limited", httpStatus: 429 };
  }
  if (status === 529) {
    return { category: "overloaded", message: "typesafe_overloaded", httpStatus: 529 };
  }
  if (status === 0) {
    return { category: "network", message: "typesafe_connection_failed" };
  }
  return { category: "network", message: `typesafe_http_${status}`, httpStatus: status };
}

export function retryDelayMs(
  attemptIndex: number,
  retryAfter: string | null,
  random: () => number,
  nowMs: number,
): number {
  const header = parseRetryAfterMs(retryAfter, nowMs);
  if (header != null) return Math.min(RETRY_CAP_MS, Math.max(0, header));
  const base = Math.min(RETRY_CAP_MS, RETRY_BASE_MS * 2 ** Math.max(0, attemptIndex));
  const unit = Math.min(1, Math.max(0, random()));
  return base + Math.floor(base * 0.2 * unit);
}

export function createTypeSafeJudgmentPort(options: TypeSafeJudgmentOptions): JudgmentPort {
  const fetchImpl = options.fetchImpl ?? fetch;
  const endpoint = options.endpoint ?? TYPESAFE_ENDPOINT;
  const maxAttempts = clampAttempts(options.maxAttempts ?? 4);

  return {
    async judge(request, signal): Promise<JudgmentResponse> {
      const key = (await options.getApiKey())?.trim() ?? "";
      if (!key) {
        return {
          ok: false,
          failure: { category: "missing_secret", message: "typesafe_key_missing" },
        };
      }
      const body = JSON.stringify(wireTypeSafeBody(request));
      return runTypeSafeAttempts({
        signal,
        questions: request.questions,
        maxAttempts,
        ...(options.random ? { random: options.random } : {}),
        ...(options.now ? { now: options.now } : {}),
        ...(options.sleep ? { sleep: options.sleep } : {}),
        perform: async () => {
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
            return {
              kind: "http",
              status: response.status,
              bodyText: text,
              retryAfter: response.headers.get("retry-after"),
              requestId: response.headers.get("x-request-id") ?? response.headers.get("request-id"),
              elapsedMs: Math.max(0, Date.now() - started),
            };
          } catch (error) {
            if (signal.aborted) return { kind: "transport", reason: "cancelled" };
            if (isTimeoutError(error)) return { kind: "transport", reason: "timeout" };
            return { kind: "transport", reason: "network" };
          }
        },
      });
    },
  };
}

export async function runTypeSafeAttempts(input: {
  readonly signal: AbortSignal;
  readonly questions: Readonly<Record<string, JudgmentQuestion>>;
  readonly maxAttempts?: number;
  readonly random?: () => number;
  readonly now?: () => number;
  readonly sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
  readonly perform: () => Promise<TypeSafeAttempt>;
}): Promise<JudgmentResponse> {
  const maxAttempts = clampAttempts(input.maxAttempts ?? 4);
  const random = input.random ?? Math.random;
  const now = input.now ?? Date.now;
  const sleep = input.sleep ?? delay;
  let last: JudgmentResponse = {
    ok: false,
    failure: { category: "network", message: "typesafe_connection_failed" },
  };

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    if (input.signal.aborted) {
      return { ok: false, failure: { category: "cancelled", message: "jev_cancelled" } };
    }
    const result = await input.perform();
    if (result.kind === "terminal") {
      return { ok: false, failure: result.failure };
    }
    if (result.kind === "transport") {
      if (result.reason === "cancelled" || input.signal.aborted) {
        return { ok: false, failure: { category: "cancelled", message: "jev_cancelled" } };
      }
      last = {
        ok: false,
        failure: {
          category: result.reason === "timeout" ? "timeout" : "network",
          message: result.reason === "timeout" ? "typesafe_timeout" : "typesafe_connection_failed",
        },
      };
      if (attempt >= maxAttempts - 1) return last;
      await sleep(retryDelayMs(attempt, null, random, now()), input.signal);
      continue;
    }

    const requestId = result.requestId?.trim() || undefined;
    if (result.status >= 200 && result.status < 300) {
      const parsed = parseTypeSafeBody(result.bodyText, result.elapsedMs, input.questions, requestId);
      if (!parsed.ok && requestId && !parsed.failure.providerRequestId) {
        return {
          ok: false,
          failure: { ...parsed.failure, providerRequestId: requestId },
        };
      }
      return parsed;
    }

    last = {
      ok: false,
      failure: {
        ...failureForHttpStatus(result.status),
        ...(requestId ? { providerRequestId: requestId } : {}),
      },
    };
    if (!isRetryableHttpStatus(result.status) || attempt >= maxAttempts - 1) return last;
    await sleep(retryDelayMs(attempt, result.retryAfter, random, now()), input.signal);
  }

  return last;
}

export function parseTypeSafeBody(
  text: string,
  elapsedMs: number,
  questions?: Readonly<Record<string, JudgmentQuestion>>,
  headerRequestId?: string,
): JudgmentResponse {
  let root: unknown;
  try {
    root = parseJsonRejectingDuplicates(text);
  } catch (error) {
    const message = error instanceof DuplicateKeyError ? "typesafe_duplicate_key" : "typesafe_invalid_json";
    return { ok: false, failure: { category: "invalid_response", message } };
  }
  if (!root || typeof root !== "object" || Array.isArray(root)) {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_invalid_json" } };
  }
  const record = root as Record<string, unknown>;
  if (typeof record.model !== "string" || !record.model.trim()) {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_missing_model" } };
  }
  if (!record.answers || typeof record.answers !== "object" || Array.isArray(record.answers)) {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_missing_answers" } };
  }

  const parsedAnswers: Record<string, JudgmentAnswer> = {};
  for (const [id, value] of Object.entries(record.answers as Record<string, unknown>)) {
    const question = questions?.[id];
    if (questions && !question) return fail(`typesafe_unknown_output_${id}`, headerRequestId).response;
    const parsed = parseAnswer(id, value, question);
    if (!parsed.ok) {
      return headerRequestId
        ? {
            ok: false,
            failure: { ...parsed.response.failure, providerRequestId: headerRequestId },
          }
        : parsed.response;
    }
    parsedAnswers[id] = parsed.answer;
  }
  if (Object.keys(parsedAnswers).length === 0) {
    return { ok: false, failure: { category: "invalid_response", message: "typesafe_empty_answers" } };
  }
  if (questions) {
    for (const id of Object.keys(questions)) {
      if (!parsedAnswers[id]) return fail(`typesafe_missing_output_${id}`, headerRequestId).response;
    }
  }

  const usage =
    record.usage && typeof record.usage === "object"
      ? (record.usage as Record<string, unknown>)
      : {};
  const providerRequestId =
    headerRequestId?.trim() ||
    (typeof record.id === "string" ? record.id : typeof record.request_id === "string" ? record.request_id : "");

  return {
    ok: true,
    success: {
      model: record.model,
      answers: parsedAnswers,
      inputTokens: numberOrZero(usage.input_tokens),
      outputTokens: numberOrZero(usage.output_tokens),
      elapsedMs: Math.max(0, elapsedMs),
      ...(providerRequestId ? { providerRequestId } : {}),
    },
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
  question: JudgmentQuestion | undefined,
): { ok: true; answer: JudgmentAnswer } | { ok: false; response: Extract<JudgmentResponse, { ok: false }> } {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return fail(`typesafe_bad_answer_${id}`);
  }
  const row = value as Record<string, unknown>;
  if (question && row.type !== question.type) return fail(`typesafe_wrong_kind_${id}`);
  if (row.type === "noul") {
    const probabilityYes = typeof row.noul === "number" ? row.noul : Number.NaN;
    if (!validateAnswerProbability(probabilityYes)) return fail(`typesafe_bad_noul_${id}`);
    if (row.confidence != null && (typeof row.confidence !== "number" || !validateAnswerProbability(row.confidence))) {
      return fail(`typesafe_bad_noul_${id}`);
    }
    return { ok: true, answer: { type: "noul", probabilityYes } };
  }
  if (row.type === "choice") {
    if (typeof row.choice !== "string" || typeof row.confidence !== "number") {
      return fail(`typesafe_bad_choice_${id}`);
    }
    const probabilities = readProbabilities(row.probabilities);
    if (!probabilities || !validateAnswerProbability(row.confidence) || !probabilitySumOk(probabilities)) {
      return fail(`typesafe_bad_choice_${id}`);
    }
    if (question?.type === "choice") {
      const allowed = new Set(Object.keys(question.criteria));
      const got = new Set(Object.keys(probabilities));
      if (allowed.size !== got.size || [...allowed].some((key) => !got.has(key))) {
        return fail(`typesafe_bad_choice_${id}`);
      }
      if (!allowed.has(row.choice)) return fail(`typesafe_bad_choice_${id}`);
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
      !Number.isFinite(row.score) ||
      typeof row.confidence !== "number" ||
      !row.legend ||
      typeof row.legend !== "object" ||
      Array.isArray(row.legend)
    ) {
      return fail(`typesafe_bad_score_${id}`);
    }
    const probabilities = readProbabilities(row.probabilities);
    if (!probabilities || !validateAnswerProbability(row.confidence) || !probabilitySumOk(probabilities)) {
      return fail(`typesafe_bad_score_${id}`);
    }
    const legend: Record<string, string> = {};
    for (const [key, label] of Object.entries(row.legend as Record<string, unknown>)) {
      if (typeof label !== "string") return fail(`typesafe_bad_score_${id}`);
      legend[key] = label;
    }
    if (question?.type === "score") {
      const expected = question.criteria.map((label, index) => [`${index}`, label] as const);
      if (expected.length < 2) return fail(`typesafe_bad_score_${id}`);
      if (row.score < 0 || row.score > expected.length - 1) return fail(`typesafe_bad_score_${id}`);
      const legendKeys = Object.keys(legend);
      const probabilityKeys = Object.keys(probabilities);
      if (legendKeys.length !== expected.length || probabilityKeys.length !== expected.length) {
        return fail(`typesafe_bad_score_${id}`);
      }
      for (const [key, label] of expected) {
        if (legend[key] !== label || probabilities[key] == null) return fail(`typesafe_bad_score_${id}`);
      }
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
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const out: Record<string, number> = {};
  for (const [key, raw] of Object.entries(value as Record<string, unknown>)) {
    if (typeof raw !== "number" || !validateAnswerProbability(raw)) return null;
    out[key] = raw;
  }
  if (Object.keys(out).length === 0) return null;
  return out;
}

function probabilitySumOk(probabilities: Readonly<Record<string, number>>): boolean {
  const sum = Object.values(probabilities).reduce((total, value) => total + value, 0);
  return Math.abs(sum - 1) <= PROBABILITY_SUM_TOLERANCE;
}

function fail(
  message: string,
  providerRequestId?: string,
): { ok: false; response: Extract<JudgmentResponse, { ok: false }> } {
  return {
    ok: false,
    response: {
      ok: false,
      failure: {
        category: "invalid_response",
        message,
        ...(providerRequestId ? { providerRequestId } : {}),
      },
    },
  };
}

function numberOrZero(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function clampAttempts(value: number): number {
  return Math.min(5, Math.max(1, Math.floor(value)));
}

function parseRetryAfterMs(value: string | null, nowMs: number): number | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (!trimmed) return null;
  if (/^\d+$/.test(trimmed)) return Number(trimmed) * 1000;
  const date = Date.parse(trimmed);
  if (Number.isFinite(date)) return Math.max(0, date - nowMs);
  return null;
}

function isTimeoutError(error: unknown): boolean {
  return error instanceof Error && error.name === "TimeoutError";
}

function delay(ms: number, signal: AbortSignal): Promise<void> {
  if (ms <= 0 || signal.aborted) return Promise.resolve();
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

class DuplicateKeyError extends Error {
  constructor() {
    super("duplicate_key");
    this.name = "DuplicateKeyError";
  }
}

function parseJsonRejectingDuplicates(text: string): unknown {
  let index = 0;
  const value = parseValue(text);
  skip(text);
  if (index !== text.length) throw new Error("trailing");
  return value;

  function skip(source: string): void {
    while (index < source.length && " \n\r\t".includes(source[index] ?? "")) index += 1;
  }

  function parseValue(source: string): unknown {
    skip(source);
    const char = source[index];
    if (char === '"') return parseString(source);
    if (char === "{") return parseObject(source);
    if (char === "[") return parseArray(source);
    if (char === "t") return literal(source, "true", true);
    if (char === "f") return literal(source, "false", false);
    if (char === "n") return literal(source, "null", null);
    return parseNumber(source);
  }

  function literal(source: string, token: string, value: unknown): unknown {
    if (source.slice(index, index + token.length) !== token) throw new Error("literal");
    index += token.length;
    return value;
  }

  function parseObject(source: string): Record<string, unknown> {
    index += 1;
    const out: Record<string, unknown> = {};
    const seen = new Set<string>();
    skip(source);
    if (source[index] === "}") {
      index += 1;
      return out;
    }
    while (index < source.length) {
      skip(source);
      if (source[index] !== '"') throw new Error("key");
      const key = parseString(source);
      if (seen.has(key)) throw new DuplicateKeyError();
      seen.add(key);
      skip(source);
      if (source[index] !== ":") throw new Error("colon");
      index += 1;
      out[key] = parseValue(source);
      skip(source);
      if (source[index] === ",") {
        index += 1;
        continue;
      }
      if (source[index] === "}") {
        index += 1;
        return out;
      }
      throw new Error("object");
    }
    throw new Error("object");
  }

  function parseArray(source: string): unknown[] {
    index += 1;
    const out: unknown[] = [];
    skip(source);
    if (source[index] === "]") {
      index += 1;
      return out;
    }
    while (index < source.length) {
      out.push(parseValue(source));
      skip(source);
      if (source[index] === ",") {
        index += 1;
        continue;
      }
      if (source[index] === "]") {
        index += 1;
        return out;
      }
      throw new Error("array");
    }
    throw new Error("array");
  }

  function parseString(source: string): string {
    index += 1;
    let out = "";
    while (index < source.length) {
      const char = source[index] ?? "";
      if (char === '"') {
        index += 1;
        return out;
      }
      if (char === "\\") {
        const next = source[index + 1] ?? "";
        const escaped: Record<string, string> = {
          '"': '"',
          "\\": "\\",
          "/": "/",
          b: "\b",
          f: "\f",
          n: "\n",
          r: "\r",
          t: "\t",
        };
        if (next === "u") {
          const hex = source.slice(index + 2, index + 6);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw new Error("unicode");
          out += String.fromCharCode(Number.parseInt(hex, 16));
          index += 6;
          continue;
        }
        if (!(next in escaped)) throw new Error("escape");
        out += escaped[next];
        index += 2;
        continue;
      }
      out += char;
      index += 1;
    }
    throw new Error("string");
  }

  function parseNumber(source: string): number {
    const start = index;
    if (source[index] === "-") index += 1;
    if (source[index] === "0") index += 1;
    else if (source[index] != null && source[index]! >= "1" && source[index]! <= "9") {
      while (source[index] != null && source[index]! >= "0" && source[index]! <= "9") index += 1;
    } else throw new Error("number");
    if (source[index] === ".") {
      index += 1;
      if (source[index] == null || source[index]! < "0" || source[index]! > "9") throw new Error("number");
      while (source[index] != null && source[index]! >= "0" && source[index]! <= "9") index += 1;
    }
    if (source[index] === "e" || source[index] === "E") {
      index += 1;
      if (source[index] === "+" || source[index] === "-") index += 1;
      if (source[index] == null || source[index]! < "0" || source[index]! > "9") throw new Error("number");
      while (source[index] != null && source[index]! >= "0" && source[index]! <= "9") index += 1;
    }
    const value = Number(source.slice(start, index));
    if (!Number.isFinite(value)) throw new Error("number");
    return value;
  }
}
