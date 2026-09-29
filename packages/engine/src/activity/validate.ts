import type { Observation, ObservationSourceType } from "@relay/contracts";
import { boundString, forbiddenKey, isIsoTimestamp, MAX_TEXT, MAX_TITLE, MAX_URI, plainObject } from "./bounds.js";
import { sanitizePageData } from "./sensitive.js";

const SOURCES = new Set<ObservationSourceType>(["windows", "chrome", "filesystem", "relay"]);
const EVENT_TYPE = /^[a-z][a-z0-9._-]{0,80}$/;

export type Validation =
  | { readonly ok: true; readonly observation: Observation }
  | { readonly ok: false; readonly reason: string };

function stripQuery(value: string | undefined): string | undefined {
  if (!value) return undefined;
  try {
    const url = new URL(value);
    url.search = "";
    url.hash = "";
    return url.toString();
  } catch {
    const cut = value.split("#")[0]?.split("?")[0];
    return cut || undefined;
  }
}

function optionalBounded(record: Record<string, unknown> | null, key: string, max: number): string | undefined {
  if (!record) return undefined;
  const value = boundString(record[key], max);
  return value ?? undefined;
}

export function validateObservation(input: unknown, id: string): Validation {
  const row = plainObject(input);
  if (!row) return { ok: false, reason: "malformed" };
  if (!isIsoTimestamp(row.timestamp)) return { ok: false, reason: "timestamp" };
  const source = plainObject(row.source);
  const sourceType = source?.type;
  if (typeof sourceType !== "string" || !SOURCES.has(sourceType as ObservationSourceType)) {
    return { ok: false, reason: "source" };
  }
  if (typeof row.eventType !== "string" || !EVENT_TYPE.test(row.eventType)) {
    return { ok: false, reason: "event_type" };
  }
  if (/^(tool|shell|exec|command)(\.|$)/.test(row.eventType)) {
    return { ok: false, reason: "event_type" };
  }
  const application = plainObject(row.application);
  const resource = plainObject(row.resource);
  const page = sanitizePageData(row.data);
  const provider = optionalBounded(source, "provider", 80);
  const deviceId = optionalBounded(source, "deviceId", 80);
  const processName = optionalBounded(application, "processName", 120);
  const executable = optionalBounded(application, "executable", MAX_URI);
  const windowTitle = optionalBounded(application, "windowTitle", MAX_TITLE);
  const uri = stripQuery(optionalBounded(resource, "uri", MAX_URI));
  const domain = optionalBounded(resource, "domain", 253)?.toLowerCase();
  const resourceTitle = optionalBounded(resource, "title", MAX_TITLE);
  const path = optionalBounded(resource, "path", MAX_URI);
  const observation: Observation = {
    id,
    timestamp: row.timestamp,
    source: {
      type: sourceType as ObservationSourceType,
      ...(provider ? { provider } : {}),
      ...(deviceId ? { deviceId } : {}),
    },
    eventType: row.eventType,
    ...(application
      ? {
          application: {
            ...(processName ? { processName } : {}),
            ...(executable ? { executable } : {}),
            ...(windowTitle ? { windowTitle } : {}),
          },
        }
      : {}),
    ...(resource
      ? {
          resource: {
            ...(uri ? { uri } : {}),
            ...(domain ? { domain } : {}),
            ...(resourceTitle ? { title: resourceTitle } : {}),
            ...(path ? { path } : {}),
          },
        }
      : {}),
    data: page.data,
    sensitivity: boundString(row.sensitivity, 40) ?? "work",
  };
  if (JSON.stringify(observation).length > MAX_TEXT * 4) return { ok: false, reason: "oversized" };
  if (Object.keys(row).some((key) => forbiddenKey(key) && key !== "data")) {
    return { ok: false, reason: "forbidden_field" };
  }
  return { ok: true, observation };
}

export function observationFingerprint(observation: Observation): string {
  return [
    observation.source.type,
    observation.eventType,
    observation.application?.processName ?? "",
    observation.application?.windowTitle ?? "",
    observation.resource?.uri ?? "",
    observation.resource?.domain ?? "",
    observation.resource?.path ?? "",
  ].join("|");
}

export function isDuplicate(history: readonly Observation[], next: Observation): boolean {
  const at = Date.parse(next.timestamp);
  const fingerprint = observationFingerprint(next);
  for (let index = history.length - 1; index >= 0 && index >= history.length - 20; index -= 1) {
    const prior = history[index];
    if (!prior || prior.data?.supersededBy) continue;
    if (Math.abs(at - Date.parse(prior.timestamp)) > 2_000) break;
    if (observationFingerprint(prior) === fingerprint) return true;
  }
  return false;
}
