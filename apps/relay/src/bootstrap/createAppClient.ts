import type { RelayClient } from "@relay/contracts";
import type { RelayEngine } from "@relay/engine";
import { createBrowserDemoClient } from "./createBrowserDemoClient";
import { createDesktopClient } from "./createDesktopClient";

export type AppClientHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  start(): Promise<void>;
  stop(): Promise<void>;
};

type TauriHost = {
  __TAURI_INTERNALS__?: { invoke?: unknown };
};

export async function createAppClient(): Promise<AppClientHandle> {
  if (isTauri()) return createDesktopClient();
  return createBrowserDemoClient();
}

function isTauri(): boolean {
  return Boolean((globalThis as TauriHost).__TAURI_INTERNALS__?.invoke);
}
