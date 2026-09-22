export type SqlRunResult = {
  readonly changes: number;
  readonly lastInsertRowid?: number | bigint;
};

export type SqlStatement = {
  run(...params: readonly unknown[]): SqlRunResult;
  get(...params: readonly unknown[]): unknown;
  all(...params: readonly unknown[]): unknown[];
};

/** Sync SQL surface shared by node:sqlite and expo-sqlite. */
export type SqlHandle = {
  exec(sql: string): void;
  prepare(sql: string): SqlStatement;
  close(): void;
};
