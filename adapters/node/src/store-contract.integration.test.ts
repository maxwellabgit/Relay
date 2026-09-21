import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { EngineStore, WorkItem } from "@relay/engine";
import { TauriEngineStore } from "@relay/adapter-tauri/engine-store";
import { MemoryEngineStore } from "@relay/testkit/browser";
import { FileArtifactStore, fileArtifactRootForDatabase } from "./file-artifacts.js";
import { SqliteEngineStore } from "./sqlite-store.js";
import { sqliteStoreInvoke } from "./store-invoke.js";

const AT = "2020-01-01T00:00:00.000Z";

type Opened = {
  readonly name: string;
  readonly durable: boolean;
  open(): Promise<{ store: EngineStore; close(): Promise<void>; reopen(): Promise<EngineStore> }>;
};

const backends: Opened[] = [
  {
    name: "memory",
    durable: false,
    async open() {
      const store = new MemoryEngineStore();
      return { store, close: async () => {}, reopen: async () => new MemoryEngineStore() };
    },
  },
  {
    name: "sqlite",
    durable: true,
    async open() {
      const dir = await mkdtemp(join(tmpdir(), "relay-contract-"));
      const path = join(dir, "state.sqlite");
      const artifacts = new FileArtifactStore(fileArtifactRootForDatabase(path));
      let store = await SqliteEngineStore.open(path, artifacts);
      return {
        store,
        close: async () => {
          store.close();
          await rm(dir, { recursive: true, force: true });
        },
        reopen: async () => {
          store.close();
          store = await SqliteEngineStore.open(path, new FileArtifactStore(fileArtifactRootForDatabase(path)));
          return store;
        },
      };
    },
  },
  {
    name: "tauri",
    durable: true,
    async open() {
      const dir = await mkdtemp(join(tmpdir(), "relay-tauri-contract-"));
      const path = join(dir, "state.sqlite");
      const artifacts = new FileArtifactStore(fileArtifactRootForDatabase(path));
      let sqlite = await SqliteEngineStore.open(path, artifacts);
      const wrap = (current: SqliteEngineStore) => new TauriEngineStore(sqliteStoreInvoke(current));
      return {
        store: wrap(sqlite),
        close: async () => {
          sqlite.close();
          await rm(dir, { recursive: true, force: true });
        },
        reopen: async () => {
          sqlite.close();
          sqlite = await SqliteEngineStore.open(path, new FileArtifactStore(fileArtifactRootForDatabase(path)));
          return wrap(sqlite);
        },
      };
    },
  },
];

describe("engine store contract", () => {
  for (const backend of backends) {
    it(`${backend.name} claims one work item exclusively`, async () => {
      const opened = await backend.open();
      try {
        await opened.store.enqueue(work("work_1"));
        const [first, second] = await Promise.all([
          opened.store.claimNext(AT, "owner_a", 30_000),
          opened.store.claimNext(AT, "owner_b", 30_000),
        ]);
        expect([first, second].filter((item) => item !== null)).toHaveLength(1);
      } finally {
        await opened.close();
      }
    });

    it(`${backend.name} keeps one glossary row per key`, async () => {
      const opened = await backend.open();
      try {
        await opened.store.learning.putMemory(memory("mem_1", "First"));
        await opened.store.learning.putMemory(memory("mem_2", "Manufacturer Suggested Retail Price"));
        const rows = await opened.store.learning.listMemories();
        expect(rows).toHaveLength(1);
        expect(rows[0]?.value.expansion).toBe("Manufacturer Suggested Retail Price");
      } finally {
        await opened.close();
      }
    });

    it(`${backend.name} rejects a stale case version without writing`, async () => {
      const opened = await backend.open();
      try {
        const created = await opened.store.createCase({
          caseId: "case_1",
          origin: "direct",
          kind: "resolve",
          priority: 1,
          at: AT,
        });
        const stale = await opened.store.updateCase(created.caseId, 0, { status: "completed", at: AT });
        expect(stale).toBeNull();
        const live = await opened.store.getCase(created.caseId);
        expect(live?.status).toBe("active");
        expect(live?.version).toBe(1);
      } finally {
        await opened.close();
      }
    });

    if (backend.durable) {
      it(`${backend.name} reloads a glossary entry after restart`, async () => {
        const opened = await backend.open();
        try {
          await opened.store.learning.putMemory(memory("mem_1", "Manufacturer Suggested Retail Price"));
          const restarted = await opened.reopen();
          const row = await restarted.learning.getMemory("glossary", "MSRP");
          expect(row?.value.expansion).toBe("Manufacturer Suggested Retail Price");
        } finally {
          await opened.close();
        }
      });
    }
  }
});

function work(workId: string): WorkItem {
  return {
    workId,
    type: "source.final",
    priority: 1,
    availableAt: AT,
    payload: {},
    createdAt: AT,
  };
}

function memory(memoryId: string, expansion: string) {
  return {
    memoryId,
    kind: "glossary" as const,
    key: "MSRP",
    value: { expansion },
    source: "explicit_user" as const,
    createdAt: AT,
  };
}
