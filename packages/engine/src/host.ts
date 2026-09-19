import type {
  RelayChange,
  RelayClient,
  RelayCommand,
  RelayCommandResult,
  RelaySnapshot,
} from "@relay/contracts";
import { RelayEngine, type EngineDeps } from "./engine.js";

export function createRelayClient(deps: EngineDeps): RelayClient {
  const engine = new RelayEngine(deps);
  return {
    start: () => engine.start(),
    stop: () => engine.stop(),
    execute: (command: RelayCommand): Promise<RelayCommandResult> => engine.execute(command),
    getSnapshot: (): Promise<RelaySnapshot> => engine.getSnapshot(),
    subscribe: (listener: (change: RelayChange) => void) => engine.subscribe(listener),
  };
}

export type { EngineDeps };
