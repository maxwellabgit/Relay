import type { EngineStore } from "./store.js";

/**
 * Portable transaction helper. Stores that support atomic multi-step writes
 * implement `runInTransaction`; others fall through to the callback directly.
 */
export async function runInTransaction<T>(store: EngineStore, work: () => Promise<T>): Promise<T> {
  if (typeof store.runInTransaction === "function") {
    return store.runInTransaction(work);
  }
  return work();
}
