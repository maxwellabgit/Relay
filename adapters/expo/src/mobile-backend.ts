import type { ArtifactStorePort } from "@relay/contracts";
import { SqliteEngineStore, type SqlHandle } from "@relay/sqlite-core";
import { EncryptedArtifactStore, type ByteFilePort, type SecretStore } from "./encrypted-artifacts.js";
import { openExpoSqliteHandle } from "./expo-sqlite.js";
import {
  createMobileLifecycle,
  createUnavailableForegroundSpeech,
  type ForegroundSpeechPort,
  type MobileLifecycle,
} from "./lifecycle.js";

export type MobileBackend = {
  readonly store: SqliteEngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly speech: ForegroundSpeechPort;
  readonly lifecycle: MobileLifecycle;
  close(): void;
};

export async function openMobileBackend(options: {
  readonly database?: SqlHandle;
  readonly openDatabase?: () => Promise<SqlHandle>;
  readonly files: ByteFilePort;
  readonly secrets: SecretStore;
  readonly speech?: ForegroundSpeechPort;
  readonly cancelGeneration?: () => void;
  readonly checkpoint?: () => Promise<void>;
}): Promise<MobileBackend> {
  const artifacts = new EncryptedArtifactStore(options.files, options.secrets);
  const database = options.database ?? (await (options.openDatabase ?? openExpoSqliteHandle)());
  const store = await SqliteEngineStore.openHandle(database, artifacts);
  const speech = options.speech ?? createUnavailableForegroundSpeech();
  const lifecycle = createMobileLifecycle({
    cancelGeneration: options.cancelGeneration ?? (() => undefined),
    stopCapture: async () => {
      await speech.stop();
    },
    checkpoint: options.checkpoint ?? (async () => undefined),
  });
  return {
    store,
    artifacts,
    speech,
    lifecycle,
    close() {
      store.close();
    },
  };
}
