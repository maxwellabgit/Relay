import type { RelayChange, RelaySnapshot } from "./changes.js";
import type { RelayCommand, RelayCommandResult } from "./commands.js";

/**
 * Shared facade for React UI and tests.
 * React must never call step/pump/runUntilIdle or storage repositories.
 * start() owns the queue-processing task independently of rendering.
 */
export type RelayClient = {
  start(): Promise<void>;
  stop(): Promise<void>;
  execute(command: RelayCommand): Promise<RelayCommandResult>;
  getSnapshot(): Promise<RelaySnapshot>;
  subscribe(listener: (change: RelayChange) => void): () => void;
};
