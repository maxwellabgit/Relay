import type { RelayClient } from "@relay/contracts";
import type { RelayEngine } from "@relay/engine";

export type BrowserDemoOptions = Record<string, never>;

export type BrowserDemoHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  readonly secrets: {
    status(): Promise<"present" | "disabled" | "unknown">;
    set(value: string): Promise<void>;
    delete(): Promise<void>;
  };
  onHostBackground(): Promise<void>;
  start(): Promise<void>;
  stop(): Promise<void>;
};

/** Production bundle target. The in-memory demo client is a different module. */
export function createBrowserDemoClient(): BrowserDemoHandle {
  throw new Error(
    "RELAY browser demo requires EXPO_PUBLIC_RELAY_ALLOW_DEMO=1. " +
      "Production paths use Tauri desktop or createMobileClient().",
  );
}
