import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import {
  ProvenanceIndex,
  type ArtifactProvenance,
  type ArtifactRef,
  type ArtifactStorePort,
  type DataPolicy,
} from "@relay/contracts";

/**
 * Durable allowlisted judgment artifacts only (no verbatim user asks).
 * Transient raw input must not use this store.
 */
export class DecisionArtifactStore implements ArtifactStorePort {
  private seq = 0;
  private readonly seals = new ProvenanceIndex();

  constructor(private readonly rootDir: string) {
    mkdirSync(rootDir, { recursive: true });
  }

  async put(value: Uint8Array, policy: DataPolicy, derivedFrom: readonly ArtifactRef[] = []): Promise<ArtifactRef> {
    const sha256 = hash(value);
    const artifactId = `artifact_${++this.seq}_${sha256.slice(0, 16)}`;
    const path = join(this.rootDir, `${artifactId}.bin`);
    if (!existsSync(path)) {
      writeFileSync(path, value);
      writeFileSync(
        `${path}.meta.json`,
        `${JSON.stringify({
          artifactId,
          sha256,
          createdAt: new Date().toISOString(),
          bytes: value.byteLength,
          allowlisted: true,
        })}\n`,
      );
    }
    const sealed = this.seals.seal(artifactId, sha256, policy, derivedFrom);
    return { artifactId, sha256, policy: sealed.policy };
  }

  async provenance(artifactId: string): Promise<ArtifactProvenance | null> {
    return this.seals.get(artifactId);
  }

  async get(ref: ArtifactRef): Promise<Uint8Array> {
    const path = join(this.rootDir, `${ref.artifactId}.bin`);
    const bytes = new Uint8Array(readFileSync(path));
    const actual = hash(bytes);
    if (actual !== ref.sha256) throw new Error("artifact_hash_mismatch");
    return bytes;
  }
}

function hash(bytes: Uint8Array): string {
  return createHash("sha256").update(Buffer.from(bytes)).digest("hex");
}

export function decisionArtifactRoot(runsRoot: string, runId: string): string {
  const root = join(runsRoot, runId, "decision-artifacts");
  mkdirSync(dirname(root), { recursive: true });
  mkdirSync(root, { recursive: true });
  return root;
}
