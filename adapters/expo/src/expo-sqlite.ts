import type { SqlHandle } from "@relay/sqlite-core";

type ExpoSqliteDatabase = {
  execSync(source: string): void;
  runSync(source: string, ...params: readonly unknown[]): { changes: number; lastInsertRowId: number };
  getFirstSync<T>(source: string, ...params: readonly unknown[]): T | null;
  getAllSync<T>(source: string, ...params: readonly unknown[]): T[];
  closeSync(): void;
};

type ExpoSqliteModule = {
  openDatabaseSync(databaseName: string): ExpoSqliteDatabase;
};

export function wrapExpoSqlite(database: ExpoSqliteDatabase): SqlHandle {
  return {
    exec(sql: string) {
      database.execSync(sql);
    },
    prepare(sql: string) {
      return {
        run(...params: readonly unknown[]) {
          const result = database.runSync(sql, ...params);
          return { changes: Number(result.changes), lastInsertRowid: Number(result.lastInsertRowId) };
        },
        get(...params: readonly unknown[]) {
          return database.getFirstSync(sql, ...params);
        },
        all(...params: readonly unknown[]) {
          return database.getAllSync(sql, ...params);
        },
      };
    },
    close() {
      database.closeSync();
    },
  };
}

/** Opens the production mobile database through expo-sqlite (sync API). */
export async function openExpoSqliteHandle(databaseName = "relay.db"): Promise<SqlHandle> {
  const sqlite = (await import("expo-sqlite")) as ExpoSqliteModule;
  return wrapExpoSqlite(sqlite.openDatabaseSync(databaseName));
}
