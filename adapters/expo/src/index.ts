/** Expo mobile adapter — durable SQLite, encrypted artifacts, foreground speech, lifecycle. */

import type { SecretStore } from "./encrypted-artifacts.js";

export type ExpoSecretStore = SecretStore;

export type ExpoSystemOneTransport = {
  systemOne(request: {
    model: string;
    payload: unknown;
  }): Promise<
    | { ok: true; body: unknown }
    | { ok: false; category: "disabled" | "missing_secret" | "network"; message: string }
  >;
};

/**
 * Missing key means disabled — never a fake positive, never compile secrets into JS.
 * Hosted requests go through createTypeSafeJudgmentPort in the mobile client.
 */
export function createExpoSystemOneTransport(secrets: ExpoSecretStore): ExpoSystemOneTransport {
  return {
    async systemOne() {
      const key = await secrets.get("typesafe_api_key");
      if (!key) {
        return { ok: false, category: "missing_secret", message: "typesafe_key_missing" };
      }
      return { ok: false, category: "disabled", message: "native_system_one_pending_dev_client" };
    },
  };
}

export {
  EncryptedArtifactStore,
  MemoryByteFiles,
  MemorySecretStore,
  type ByteFilePort,
  type SecretStore,
} from "./encrypted-artifacts.js";
export { openExpoSqliteHandle, wrapExpoSqlite } from "./expo-sqlite.js";
export {
  createMobileLifecycle,
  createUnavailableForegroundSpeech,
  type ForegroundSpeechPort,
  type MobileLifecycle,
  type SpeechStatus,
} from "./lifecycle.js";
export { openMobileBackend, type MobileBackend } from "./mobile-backend.js";
export { createExpoDocumentFiles, createExpoSecureSecretStore } from "./durable-host.js";

export const EXPO_ADAPTER_BOOTSTRAP = "1.0.0" as const;
