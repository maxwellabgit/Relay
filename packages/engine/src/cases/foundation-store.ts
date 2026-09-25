import type { EngineStore } from "../store.js";

export type FoundationRow = {
  readonly id: string;
  readonly version: number;
  readonly payload: unknown;
  readonly updatedAt: string;
};

export type FoundationStore = {
  put(kind: string, id: string, version: number, payload: unknown, at: string): Promise<void>;
  get(kind: string, id: string): Promise<FoundationRow | null>;
  list(kind: string): Promise<readonly FoundationRow[]>;
  linkExecution(executionId: string, projectCaseId: string, at: string): Promise<void>;
  listCaseLinks(executionId: string): Promise<readonly string[]>;
  /** Insert once. A second call with the same kind and id does not overwrite. */
  insertIfAbsent(kind: string, id: string, version: number, payload: unknown, at: string): Promise<boolean>;
};

type MemoryLink = { executionId: string; projectCaseId: string; at: string };

export class MemoryFoundationStore implements FoundationStore {
  private readonly rows = new Map<string, FoundationRow>();
  private readonly links: MemoryLink[] = [];

  async put(kind: string, id: string, version: number, payload: unknown, at: string): Promise<void> {
    this.rows.set(`${kind}:${id}`, { id, version, payload, updatedAt: at });
  }

  async get(kind: string, id: string): Promise<FoundationRow | null> {
    return this.rows.get(`${kind}:${id}`) ?? null;
  }

  async list(kind: string): Promise<readonly FoundationRow[]> {
    const prefix = `${kind}:`;
    return [...this.rows.entries()].filter(([key]) => key.startsWith(prefix)).map((entry) => entry[1]);
  }

  async linkExecution(executionId: string, projectCaseId: string, at: string): Promise<void> {
    if (this.links.some((link) => link.executionId === executionId && link.projectCaseId === projectCaseId)) return;
    this.links.push({ executionId, projectCaseId, at });
  }

  async listCaseLinks(executionId: string): Promise<readonly string[]> {
    return this.links.filter((link) => link.executionId === executionId).map((link) => link.projectCaseId);
  }

  async insertIfAbsent(kind: string, id: string, version: number, payload: unknown, at: string): Promise<boolean> {
    const key = `${kind}:${id}`;
    if (this.rows.has(key)) return false;
    this.rows.set(key, { id, version, payload, updatedAt: at });
    return true;
  }
}

export function foundationFromEngineStore(store: EngineStore): FoundationStore | null {
  if (
    !store.putFoundation ||
    !store.getFoundation ||
    !store.listFoundation ||
    !store.linkExecutionCase ||
    !store.listExecutionCases
  ) {
    return null;
  }
  return {
    put: (kind, id, version, payload, at) => store.putFoundation!(kind, id, version, payload, at),
    get: (kind, id) => store.getFoundation!(kind, id),
    list: (kind) => store.listFoundation!(kind),
    linkExecution: (executionId, projectCaseId, at) => store.linkExecutionCase!(executionId, projectCaseId, at),
    listCaseLinks: (executionId) => store.listExecutionCases!(executionId),
    insertIfAbsent: async (kind, id, version, payload, at) => {
      if (store.claimFoundation) return store.claimFoundation(kind, id, version, payload, at);
      const existing = await store.getFoundation!(kind, id);
      if (existing) return false;
      await store.putFoundation!(kind, id, version, payload, at);
      return true;
    },
  };
}
