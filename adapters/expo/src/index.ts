/** Expo mobile adapter — durable SQLite, encrypted artifacts, foreground speech, lifecycle. */

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
export { createDeviceForegroundSpeech, createDeviceTextModel, deviceModelStatus } from "./device-runtime.js";

export const EXPO_ADAPTER_BOOTSTRAP = "1.0.0" as const;
