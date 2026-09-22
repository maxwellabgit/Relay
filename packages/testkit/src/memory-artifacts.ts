import { ProvenanceIndex, type ArtifactProvenance, type ArtifactRef, type ArtifactStorePort, type DataPolicy } from "@relay/contracts";

export class MemoryArtifactStore implements ArtifactStorePort {
  private readonly blobs = new Map<string, Uint8Array>();
  private readonly seals = new ProvenanceIndex();

  async put(value: Uint8Array, policy: DataPolicy, derivedFrom: readonly ArtifactRef[] = []): Promise<ArtifactRef> {
    const sha256 = await hash(value);
    const artifactId = `artifact_${sha256.slice(0, 24)}`;
    if (!this.blobs.has(artifactId)) {
      this.blobs.set(artifactId, value);
    }
    const sealed = this.seals.seal(artifactId, sha256, policy, derivedFrom);
    return { artifactId, sha256, policy: sealed.policy };
  }

  async provenance(artifactId: string): Promise<ArtifactProvenance | null> {
    return this.seals.get(artifactId);
  }

  async get(ref: ArtifactRef): Promise<Uint8Array> {
    const value = this.blobs.get(ref.artifactId);
    if (!value) throw new Error(`artifact_missing:${ref.artifactId}`);
    const actual = await hash(value);
    if (actual !== ref.sha256) throw new Error("artifact_hash_mismatch");
    return value;
  }

  /** Test helper: overwrite protected bytes without updating the hash (corruption). */
  corrupt(artifactId: string, bytes: Uint8Array): void {
    this.blobs.set(artifactId, bytes);
  }

  delete(artifactId: string): void {
    this.blobs.delete(artifactId);
  }
}

async function hash(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}
