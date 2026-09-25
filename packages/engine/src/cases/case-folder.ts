import type { ArtifactStorePort } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";

/** Folder bytes stay in the artifact store. The folder only keeps a pointer. */
export class SealedCaseFolder implements CaseFolderPort {
  private readonly pointers = new Map<string, { artifactId: string; sha256: string }>();

  constructor(private readonly artifacts: ArtifactStorePort) {}

  async writeAtomic(projectCaseId: string, relativePath: string, bytes: Uint8Array): Promise<void> {
    if (!SAFE_PATH.test(relativePath)) throw new Error("unsafe_case_path");
    const ref = await this.artifacts.put(bytes, localOnlyPolicy());
    this.pointers.set(`${projectCaseId}/${relativePath}`, { artifactId: ref.artifactId, sha256: ref.sha256 });
  }

  async read(projectCaseId: string, relativePath: string): Promise<Uint8Array | null> {
    const pointer = this.pointers.get(`${projectCaseId}/${relativePath}`);
    if (!pointer) return null;
    return this.artifacts.get({ artifactId: pointer.artifactId, sha256: pointer.sha256, policy: localOnlyPolicy() });
  }

  async recover(): Promise<number> {
    return 0;
  }
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
