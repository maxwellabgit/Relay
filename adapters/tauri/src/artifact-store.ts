import {
  combinePolicies,
  localOnlyPolicy,
  ProvenanceIndex,
  type ArtifactProvenance,
  type ArtifactRef,
  type ArtifactStorePort,
  type DataPolicy,
} from "@relay/contracts";

type TauriInvoke = (command: string, args?: Record<string, unknown>) => Promise<unknown>;

/**
 * Windows production artifact store: DPAPI-protected content-addressed objects via Tauri.
 * Engine never sees the filesystem path (%LOCALAPPDATA%\RELAY\objects\).
 */
export class TauriArtifactStore implements ArtifactStorePort {
  private readonly seals = new ProvenanceIndex();

  constructor(private readonly invoke: TauriInvoke) {}

  async put(value: Uint8Array, policy: DataPolicy, derivedFrom: readonly ArtifactRef[] = []): Promise<ArtifactRef> {
    let effective = policy;
    for (const ref of derivedFrom) {
      const known = await this.provenance(ref.artifactId);
      effective = combinePolicies(effective, known && known.sha256 === ref.sha256 ? known.policy : localOnlyPolicy());
    }
    const result = (await this.invoke("artifact_put", {
      request: {
        bytes_b64: bytesToBase64(value),
        policy: effective,
      },
    })) as { artifact_id: string; sha256: string; policy?: DataPolicy };
    if (!result?.artifact_id || !result?.sha256) {
      throw new Error("artifact_put_invalid");
    }
    const stored = result.policy ?? effective;
    const sealed = this.seals.seal(result.artifact_id, result.sha256, stored, derivedFrom);
    return {
      artifactId: result.artifact_id,
      sha256: result.sha256,
      policy: sealed.policy,
    };
  }

  async provenance(artifactId: string): Promise<ArtifactProvenance | null> {
    const cached = this.seals.get(artifactId);
    if (cached) return cached;
    const result = (await this.invoke("artifact_provenance", { request: { artifact_id: artifactId } })) as
      | ArtifactProvenance
      | null;
    if (result?.artifactId && result.sha256 && result.policy) {
      this.seals.load(result);
      return this.seals.get(artifactId);
    }
    return null;
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
