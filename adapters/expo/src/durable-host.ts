import { Directory, File, Paths } from "expo-file-system";
import * as SecureStore from "expo-secure-store";
import type { ByteFilePort, SecretStore } from "./encrypted-artifacts.js";

const ARTIFACT_DIR = "relay-artifacts";

/**
 * Device document directory for encrypted artifact bytes.
 * Native only — the web file-system module does not persist.
 */
export function createExpoDocumentFiles(): ByteFilePort {
  return {
    async write(name, bytes) {
      const dir = new Directory(Paths.document, ARTIFACT_DIR);
      if (!dir.exists) dir.create({ intermediates: true, idempotent: true });
      const file = new File(dir, safeName(name));
      if (!file.exists) file.create({ intermediates: true });
      file.write(bytes);
    },
    async read(name) {
      const file = new File(Paths.document, ARTIFACT_DIR, safeName(name));
      if (!file.exists) return null;
      return new Uint8Array(await file.bytes());
    },
  };
}

/** Keychain / keystore secrets. Keys stay on device and out of JS bundles. */
export function createExpoSecureSecretStore(): SecretStore {
  return {
    async get(key) {
      return SecureStore.getItemAsync(secureKey(key));
    },
    async set(key, value) {
      await SecureStore.setItemAsync(secureKey(key), value);
    },
    async delete(key) {
      await SecureStore.deleteItemAsync(secureKey(key));
    },
  };
}

function safeName(name: string): string {
  return name.replace(/[^a-zA-Z0-9._-]/g, "_");
}

function secureKey(key: string): string {
  const cleaned = key.replace(/[^a-zA-Z0-9._-]/g, "_");
  return cleaned.length > 0 ? cleaned : "relay_secret";
}
