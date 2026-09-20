import type { ArtifactRef, ArtifactStorePort, DataPolicy } from "@relay/contracts";

export class MemoryArtifactStore implements ArtifactStorePort {
  private readonly blobs = new Map<string, Uint8Array>();

  async put(value: Uint8Array, policy: DataPolicy): Promise<ArtifactRef> {
    const sha256 = await hash(value);
    const artifactId = `artifact_${sha256.slice(0, 24)}`;
    if (!this.blobs.has(artifactId)) {
      this.blobs.set(artifactId, value);
    }
    return { artifactId, sha256, policy };
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
