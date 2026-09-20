import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import type { ArtifactRef, ArtifactStorePort, DataPolicy } from "@relay/contracts";

/**
 * Durable content-addressed artifact store for Node/Windows-equivalent harnesses.
 * Plaintext on disk is acceptable in test harnesses; production Windows uses DPAPI via Tauri.
 */
export class FileArtifactStore implements ArtifactStorePort {
  constructor(private readonly rootDir: string) {
    mkdirSync(rootDir, { recursive: true });
  }

  async put(value: Uint8Array, policy: DataPolicy): Promise<ArtifactRef> {
    const sha256 = hash(value);
    const artifactId = `artifact_${sha256.slice(0, 24)}`;
    const path = join(this.rootDir, `${artifactId}.bin`);
    if (!existsSync(path)) {
      atomicWrite(path, Buffer.from(value));
    }
    return { artifactId, sha256, policy };
  }

  async get(ref: ArtifactRef): Promise<Uint8Array> {
    const path = join(this.rootDir, `${ref.artifactId}.bin`);
    if (!existsSync(path)) throw new Error(`artifact_missing:${ref.artifactId}`);
    const bytes = new Uint8Array(readFileSync(path));
    const actual = hash(bytes);
    if (actual !== ref.sha256) throw new Error("artifact_hash_mismatch");
    return bytes;
  }
}

export function fileArtifactRootForDatabase(databasePath: string): string {
  if (databasePath === ":memory:") {
    throw new Error("file_artifacts_require_path");
  }
  const root = join(dirname(databasePath), "objects");
  mkdirSync(root, { recursive: true });
  return root;
}

function hash(bytes: Uint8Array): string {
  return createHash("sha256").update(Buffer.from(bytes)).digest("hex");
}

function atomicWrite(path: string, bytes: Buffer): void {
  const parent = dirname(path);
  mkdirSync(parent, { recursive: true });
  const tmp = join(parent, `.${path.split(/[/\\]/).pop() ?? "artifact"}.tmp`);
  writeFileSync(tmp, bytes);
  try {
    renameSync(tmp, path);
  } catch {
    writeFileSync(path, bytes);
    try {
      unlinkSync(tmp);
    } catch {
      /* ignore */
    }
  }
}
