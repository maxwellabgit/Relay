const ISO = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?Z$/;

export const MAX_NATIVE_BYTES = 256 * 1024;
export const MAX_TEXT = 4_000;
export const MAX_TITLE = 300;
export const MAX_URI = 2_000;
export const CORRELATION_MS = 20_000;
export const DEDUPE_MS = 2_000;
/** A focus gap at least this long closes the open episode. */
export const EPISODE_GAP_MS = 15 * 60 * 1000;
/** A switch away shorter than this is folded back into the surrounding episode. */
export const EPISODE_BLIP_MS = 3 * 60 * 1000;
export const JOB_APPLICATION_MIN_SPAN_MS = 3 * 60 * 1000;

const FORBIDDEN_KEYS = new Set([
  "password",
  "passwd",
  "pwd",
  "secret",
  "token",
  "authorization",
  "cookie",
  "set-cookie",
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
]);

export function isIsoTimestamp(value: unknown): value is string {
  return typeof value === "string" && ISO.test(value) && !Number.isNaN(Date.parse(value));
}

export function boundString(value: unknown, max: number): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.split("\u0000").join("").trim();
  if (!trimmed) return null;
  return trimmed.slice(0, max);
}

export function forbiddenKey(key: string): boolean {
  const normalized = key.replace(/[^a-z]/gi, "").toLowerCase();
  return FORBIDDEN_KEYS.has(key.toLowerCase()) || FORBIDDEN_KEYS.has(normalized);
}

export function scrubText(value: string): string {
  return value
    .replace(/\b(?:\d[ -]*?){13,19}\b/g, "[redacted]")
    .replace(/\bBearer\s+[A-Za-z0-9._~+/-]+=*/gi, "Bearer [redacted]")
    .slice(0, MAX_TEXT);
}

export function plainObject(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  return value as Record<string, unknown>;
}
