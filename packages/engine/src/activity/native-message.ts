import type { RelayCommand } from "@relay/contracts";
import { MAX_NATIVE_BYTES, plainObject } from "./bounds.js";

const ALLOWED = new Set([
  "chrome.tab.activated",
  "chrome.navigation",
  "chrome.page.metadata",
  "chrome.page.classified",
  "chrome.permission.changed",
  "chrome.hello",
]);

export type NativeDecision =
  | { readonly ok: true; readonly kind: "hello" }
  | { readonly ok: true; readonly kind: "observation"; readonly observation: unknown }
  | { readonly ok: false; readonly reason: string };

export function validateNativeMessage(input: unknown, byteLength: number): NativeDecision {
  if (byteLength > MAX_NATIVE_BYTES) return { ok: false, reason: "oversized" };
  const row = plainObject(input);
  if (!row) return { ok: false, reason: "malformed" };
  if (typeof row.type !== "string" || !ALLOWED.has(row.type)) return { ok: false, reason: "type" };
  if ("command" in row || "tool" in row || "invoke" in row || "shell" in row) {
    return { ok: false, reason: "forbidden_field" };
  }
  if (row.type === "chrome.hello") return { ok: true, kind: "hello" };
  const observation = plainObject(row.observation);
  if (!observation) return { ok: false, reason: "observation" };
  if (observation.eventType !== row.type) return { ok: false, reason: "event_type" };
  const source = plainObject(observation.source);
  if (source?.type != null && source.type !== "chrome") return { ok: false, reason: "source" };
  return {
    ok: true,
    kind: "observation",
    observation: {
      ...observation,
      source: { type: "chrome", provider: "extension" },
    },
  };
}

export function hostEventToCommand(payload: unknown): RelayCommand | null {
  const row = plainObject(payload);
  if (!row || typeof row.kind !== "string") return null;
  if (row.kind === "tool.execute" || row.kind === "command" || row.kind === "shell") return null;
  if (row.kind === "observation") {
    return { type: "IngestActivityObservation", observation: row.observation };
  }
  if (row.kind === "windows-status" && (row.status === "running" || row.status === "stopped" || row.status === "unavailable")) {
    return { type: "SetActivityObserverStatus", windows: row.status };
  }
  if (
    row.kind === "chrome-status" &&
    (row.status === "connected" || row.status === "disconnected" || row.status === "stopped")
  ) {
    return { type: "SetActivityObserverStatus", chrome: row.status };
  }
  return null;
}
