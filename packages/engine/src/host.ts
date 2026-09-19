import type {
  RelayChange,
  RelayClient,
  RelayCommand,
  RelayCommandResult,
  RelaySnapshot,
} from "@relay/contracts";
import { RelayEngine, type EngineDeps } from "./engine.js";

export function createRelayClientFromEngine(engine: RelayEngine): RelayClient {
  return {
    start: () => engine.start(),
    stop: () => engine.stop(),
    execute: (command: RelayCommand): Promise<RelayCommandResult> => engine.execute(command),
    getSnapshot: (): Promise<RelaySnapshot> => engine.getSnapshot(),
    subscribe: (listener: (change: RelayChange) => void) => engine.subscribe(listener),
  };
}

export function createRelayClient(deps: EngineDeps): RelayClient {
  return createRelayClientFromEngine(new RelayEngine(deps));
}

export type { EngineDeps };
