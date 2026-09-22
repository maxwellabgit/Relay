import type { RelayClient } from "@relay/contracts";
import type { RelayEngine } from "@relay/engine";

export type MobileClientHandle = {
  readonly client: RelayClient;
  readonly engine: RelayEngine;
  start(): Promise<void>;
  stop(): Promise<void>;
};

/** Web/desktop bundles must not load expo-sqlite. Native uses createMobileClient.native.ts. */
export async function createMobileClient(): Promise<MobileClientHandle> {
  throw new Error(
    "createMobileClient() is the native Expo composition. Web builds use the desktop client or an explicit demo flag.",
  );
}
