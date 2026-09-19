import { useEffect, useRef, useState } from "react";
import { StatusBar } from "expo-status-bar";
import type { RelayClient, RelaySnapshot } from "@relay/contracts";
import { RelayWorkbench } from "@relay/ui";
import { createWebClient } from "./bootstrap/createWebClient";

const EMPTY_SNAPSHOT: RelaySnapshot = {
  listening: false,
  feedItems: [],
  approvals: [],
  connections: [],
  reflexes: [],
  providerHealth: [],
  waits: [],
  status: [],
  sourceSegments: [],
  cases: [],
  queueDepth: 0,
};

export function App() {
  const clientRef = useRef<RelayClient | null>(null);
  const [snapshot, setSnapshot] = useState<RelaySnapshot>(EMPTY_SNAPSHOT);
  const [traceLines, setTraceLines] = useState<string[]>([]);

  useEffect(() => {
    const handle = createWebClient();
    clientRef.current = handle.client;

    const unsubscribe = handle.client.subscribe((change) => {
      if (change.type === "SnapshotReplaced") {
        setSnapshot(change.snapshot);
        return;
      }
      if (change.type === "TraceAppended") {
        setTraceLines((prev) => [
          ...prev.slice(-199),
          `#${change.sequence} ${change.eventType}`,
        ]);
      }
    });

    void handle.start();

    return () => {
      unsubscribe();
      clientRef.current = null;
      void handle.stop();
    };
  }, []);

  return (
    <>
      <RelayWorkbench
        snapshot={snapshot}
        onListenChange={(enabled) => {
          void clientRef.current?.execute({ type: "SetListening", enabled });
        }}
        onSubmit={(text) => {
          void clientRef.current?.execute({ type: "SubmitText", text });
        }}
        traceLines={traceLines}
        onReplayFixture={(fixture, speed) => {
          setTraceLines((prev) => [
            ...prev.slice(-199),
            `fixture:${fixture}@${speed}x (not wired)`,
          ]);
        }}
      />
      <StatusBar style="light" />
    </>
  );
}
