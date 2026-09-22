import { DatabaseSync } from "node:sqlite";
import type { ArtifactStorePort } from "@relay/contracts";
import { SqliteEngineStore, type SqlHandle } from "@relay/sqlite-core";

export { SqliteEngineStore };

export function wrapNodeSqlite(filename = ":memory:"): SqlHandle {
  const db = new DatabaseSync(filename);
  return {
    exec(sql: string) {
      db.exec(sql);
    },
    prepare(sql: string) {
      const statement = db.prepare(sql);
      return {
        run(...params: readonly unknown[]) {
          const result = statement.run(...(params as never[]));
          return { changes: Number(result.changes), lastInsertRowid: Number(result.lastInsertRowid) };
        },
        get(...params: readonly unknown[]) {
          return statement.get(...(params as never[]));
        },
        all(...params: readonly unknown[]) {
          return statement.all(...(params as never[]));
        },
      };
    },
    close() {
      db.close();
    },
  };
}

export async function openSqliteEngineStore(
  filename = ":memory:",
  artifacts?: ArtifactStorePort,
): Promise<SqliteEngineStore> {
  return SqliteEngineStore.openHandle(wrapNodeSqlite(filename), artifacts);
}
