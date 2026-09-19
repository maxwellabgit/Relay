import type { ArtifactRef, ArtifactStorePort, DataPolicy } from "@relay/contracts";

export class MemoryArtifactStore implements ArtifactStorePort {
  private readonly blobs = new Map<string, Uint8Array>();
  private seq = 0;

  async put(value: Uint8Array, policy: DataPolicy): Promise<ArtifactRef> {
    const artifactId = `art_${++this.seq}`;
    const sha256 = await hash(value);
    this.blobs.set(artifactId, value);
    return { artifactId, sha256, policy };
  }

  async get(ref: ArtifactRef): Promise<Uint8Array> {
    const value = this.blobs.get(ref.artifactId);
    if (!value) throw new Error(`artifact_missing:${ref.artifactId}`);
    return value;
  }
}

async function hash(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}
