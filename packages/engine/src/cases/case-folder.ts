import type { ArtifactStorePort } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";

/**
 * Durable Case folder. File bytes are sealed. Read returns the original Markdown.
 * Pointers live in the foundation store so a new process can open the same files.
 */
export class SealedCaseFolder implements CaseFolderPort {
  constructor(
    private readonly artifacts: ArtifactStorePort,
    private readonly records: {
      put(kind: string, id: string, version: number, payload: unknown, at: string): Promise<void>;
      get(kind: string, id: string): Promise<{ payload: unknown } | null>;
    },
    private readonly now: () => string = () => new Date().toISOString(),
  ) {}

  async writeAtomic(projectCaseId: string, relativePath: string, bytes: Uint8Array): Promise<void> {
    if (!SAFE_PATH.test(relativePath)) throw new Error("unsafe_case_path");
    const ref = await this.artifacts.put(seal(bytes), localOnlyPolicy());
    await this.records.put(
      "case_file",
      `${projectCaseId}/${relativePath}`,
      1,
      { artifactId: ref.artifactId, sha256: ref.sha256 },
      this.now(),
    );
  }

  async read(projectCaseId: string, relativePath: string): Promise<Uint8Array | null> {
    const row = await this.records.get("case_file", `${projectCaseId}/${relativePath}`);
    const pointer = row?.payload as { artifactId?: string; sha256?: string } | undefined;
    if (!pointer?.artifactId || !pointer.sha256) return null;
    const sealed = await this.artifacts.get({
      artifactId: pointer.artifactId,
      sha256: pointer.sha256,
      policy: localOnlyPolicy(),
    });
    return unseal(sealed);
  }

  async recover(): Promise<number> {
    return 0;
  }
}

function seal(bytes: Uint8Array): Uint8Array {
  const out = new Uint8Array(bytes.length + 4);
  out.set([0x52, 0x53, 0x45, 0x31]);
  for (let i = 0; i < bytes.length; i += 1) out[i + 4] = (bytes[i] ?? 0) ^ 0x5a;
  return out;
}

function unseal(bytes: Uint8Array): Uint8Array {
  if (bytes.length < 4 || bytes[0] !== 0x52 || bytes[1] !== 0x53 || bytes[2] !== 0x45 || bytes[3] !== 0x31) {
    return bytes;
  }
  const out = new Uint8Array(bytes.length - 4);
  for (let i = 0; i < out.length; i += 1) out[i] = (bytes[i + 4] ?? 0) ^ 0x5a;
  return out;
}

export type CaseFolderPort = {
  writeAtomic(projectCaseId: string, relativePath: string, bytes: Uint8Array): Promise<void>;
  read(projectCaseId: string, relativePath: string): Promise<Uint8Array | null>;
  recover(): Promise<number>;
};

const SAFE_PATH = /^(main\.md|rules\.json|references\.json|patterns\.md)$/;

export class MemoryCaseFolder implements CaseFolderPort {
  private readonly files = new Map<string, Uint8Array>();
  private journal: { projectCaseId: string; relativePath: string; bytes: Uint8Array } | null = null;

  async writeAtomic(projectCaseId: string, relativePath: string, bytes: Uint8Array): Promise<void> {
    if (!SAFE_PATH.test(relativePath)) throw new Error("unsafe_case_path");
    this.journal = { projectCaseId, relativePath, bytes };
    this.files.set(this.key(projectCaseId, `${relativePath}.tmp`), bytes);
    this.files.set(this.key(projectCaseId, relativePath), bytes);
    this.files.delete(this.key(projectCaseId, `${relativePath}.tmp`));
    this.journal = null;
  }

  async read(projectCaseId: string, relativePath: string): Promise<Uint8Array | null> {
    return this.files.get(this.key(projectCaseId, relativePath)) ?? null;
  }

  /** Test hook: leave a journal entry so recover() must finish the write. */
  interruptAfterJournal(projectCaseId: string, relativePath: string, bytes: Uint8Array): void {
    if (!SAFE_PATH.test(relativePath)) throw new Error("unsafe_case_path");
    this.journal = { projectCaseId, relativePath, bytes };
  }

  async recover(): Promise<number> {
    if (!this.journal) return 0;
    const pending = this.journal;
    await this.writeAtomic(pending.projectCaseId, pending.relativePath, pending.bytes);
    return 1;
  }

  private key(projectCaseId: string, relativePath: string): string {
    return `${projectCaseId}/${relativePath}`;
  }
}
