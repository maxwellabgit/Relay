import type { ArtifactRef, ArtifactStorePort, DataPolicy } from "@relay/contracts";

type TauriInvoke = (command: string, args?: Record<string, unknown>) => Promise<unknown>;

/**
 * Windows production artifact store: DPAPI-protected content-addressed objects via Tauri.
 * Engine never sees the filesystem path (%LOCALAPPDATA%\RELAY\objects\).
 */
export class TauriArtifactStore implements ArtifactStorePort {
  constructor(private readonly invoke: TauriInvoke) {}

  async put(value: Uint8Array, policy: DataPolicy): Promise<ArtifactRef> {
    const result = (await this.invoke("artifact_put", {
      request: {
        bytes_b64: bytesToBase64(value),
        policy,
      },
    })) as { artifact_id: string; sha256: string };
    if (!result?.artifact_id || !result?.sha256) {
      throw new Error("artifact_put_invalid");
    }
    return {
      artifactId: result.artifact_id,
      sha256: result.sha256,
      policy,
    };
  }

  async get(ref: ArtifactRef): Promise<Uint8Array> {
    try {
      const result = (await this.invoke("artifact_get", {
        request: {
          artifact_id: ref.artifactId,
          sha256: ref.sha256,
        },
      })) as { bytes_b64: string };
      if (!result?.bytes_b64) {
        throw new Error("artifact_missing");
      }
      return base64ToBytes(result.bytes_b64);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      if (message.includes("artifact_hash_mismatch")) {
        throw new Error("artifact_hash_mismatch");
      }
      if (message.includes("artifact_missing")) {
        throw new Error(`artifact_missing:${ref.artifactId}`);
      }
      throw error instanceof Error ? error : new Error(message);
    }
  }
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i++) {
    binary += String.fromCharCode(bytes[i]!);
  }
  return btoa(binary);
}

function base64ToBytes(value: string): Uint8Array {
  const binary = atob(value);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    out[i] = binary.charCodeAt(i);
  }
  return out;
}
