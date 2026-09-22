import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import {
  ProvenanceIndex,
  type ArtifactProvenance,
  type ArtifactRef,
  type ArtifactStorePort,
  type DataPolicy,
} from "@relay/contracts";

/**
 * Durable content-addressed artifact store for Node/Windows-equivalent harnesses.
 * Plaintext on disk is acceptable in test harnesses; production Windows uses DPAPI via Tauri.
 */
export class FileArtifactStore implements ArtifactStorePort {
  private readonly seals = new ProvenanceIndex();

  constructor(private readonly rootDir: string) {
    mkdirSync(rootDir, { recursive: true });
  }

  async put(value: Uint8Array, policy: DataPolicy, derivedFrom: readonly ArtifactRef[] = []): Promise<ArtifactRef> {
    const sha256 = hash(value);
    const artifactId = `artifact_${sha256.slice(0, 24)}`;
    const path = join(this.rootDir, `${artifactId}.bin`);
    if (!existsSync(path)) {
      atomicWrite(path, Buffer.from(value));
    }
    this.hydrate(artifactId);
    const sealed = this.seals.seal(artifactId, sha256, policy, derivedFrom);
    writeFileSync(join(this.rootDir, `${artifactId}.prov.json`), `${JSON.stringify(sealed)}\n`);
    return { artifactId, sha256, policy: sealed.policy };
  }

  async provenance(artifactId: string): Promise<ArtifactProvenance | null> {
    this.hydrate(artifactId);
    return this.seals.get(artifactId);
  }

  private hydrate(artifactId: string): void {
    if (this.seals.get(artifactId)) return;
    const path = join(this.rootDir, `${artifactId}.prov.json`);
    if (!existsSync(path)) return;
    const parsed = JSON.parse(readFileSync(path, "utf8")) as ArtifactProvenance;
    if (parsed?.artifactId && parsed.sha256 && parsed.policy) this.seals.load(parsed);
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
