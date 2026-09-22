import type { RelayClient } from "@relay/contracts";
import type { RelayEngine } from "@relay/engine";
import { Platform } from "react-native";
import { createDesktopClient } from "./createDesktopClient";
import { createMobileClient } from "./createMobileClient";

export type AppSecretControl = {
  status(): Promise<"present" | "disabled" | "unknown">;
  set(value: string): Promise<void>;
  delete(): Promise<void>;
};

export type AppClientHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  readonly secrets: AppSecretControl;
  onHostBackground(): Promise<void>;
  start(): Promise<void>;
  stop(): Promise<void>;
};

type TauriHost = {
  __TAURI_INTERNALS__?: { invoke?: unknown };
};

/**
 * Selects the production client for the current host.
 * - Tauri → desktop production composition
 * - Native iOS/Android → fail closed until createMobileClient (F2); demo only with explicit flag
 * - Web / other → in-memory demo only on the internal channel with EXPO_PUBLIC_RELAY_ALLOW_DEMO=1
 */
export async function createAppClient(): Promise<AppClientHandle> {
  if (isTauri()) return createDesktopClient();

  if (isNativeMobile()) return createMobileClient();

  if (isExplicitDemoAllowed()) {
    const { createBrowserDemoClient } = await import("./createBrowserDemoClient.js");
    return createBrowserDemoClient();
  }

  throw new Error(
    "RELAY browser demo requires EXPO_PUBLIC_RELAY_ALLOW_DEMO=1. " +
      "Production paths use Tauri desktop or createMobileClient().",
  );
}

export function isExplicitDemoAllowed(): boolean {
  const env = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env;
  if (env?.EXPO_PUBLIC_RELAY_CHANNEL !== "internal") return false;
  const value = env.EXPO_PUBLIC_RELAY_ALLOW_DEMO;
  return value === "1" || value === "true";
}

function isTauri(): boolean {
  return Boolean((globalThis as TauriHost).__TAURI_INTERNALS__?.invoke);
}

function isNativeMobile(): boolean {
  return Platform.OS === "ios" || Platform.OS === "android";
}
