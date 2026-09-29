import { boundString, forbiddenKey, MAX_TEXT, plainObject, scrubText } from "./bounds.js";

export type SanitizedPage = {
  readonly data: Record<string, unknown>;
  readonly ignoredSensitive: boolean;
};

function sanitizeValue(value: unknown, depth: number): unknown {
  if (depth > 5) return null;
  if (typeof value === "string") return scrubText(value).slice(0, MAX_TEXT);
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  if (typeof value === "boolean") return value;
  if (Array.isArray(value)) {
    return value.slice(0, 8).map((item) => sanitizeValue(item, depth + 1));
  }
  const record = plainObject(value);
  if (!record) return null;
  const next: Record<string, unknown> = {};
  for (const [key, item] of Object.entries(record)) {
    if (forbiddenKey(key)) continue;
    if (key === "type" && (item === "password" || item === "cc-number" || item === "cc-csc")) continue;
    const cleaned = sanitizeValue(item, depth + 1);
    if (cleaned != null) next[key] = cleaned;
  }
  return next;
}

/** Drops password, card, and credential fields. Page text stays data, never a command. */
export function sanitizePageData(input: unknown): SanitizedPage {
  const record = plainObject(input) ?? {};
  let ignoredSensitive = false;
  if (Array.isArray(record.fields)) {
    ignoredSensitive = record.fields.some((field) => {
      const row = plainObject(field);
      const type = row?.type;
      return type === "password" || type === "cc-number" || type === "cc-csc";
    });
  }
  if (record.sensitiveInputsIgnored === true) ignoredSensitive = true;
  const cleaned = sanitizeValue(record, 0);
  const data = plainObject(cleaned) ?? {};
  delete data.fields;
  delete data.value;
  delete data.values;
  const text = boundString(data.text, MAX_TEXT);
  if (text) data.text = text;
  else delete data.text;
  return { data, ignoredSensitive };
}
