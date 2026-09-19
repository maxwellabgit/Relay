import { useEffect, useRef, useState } from "react";
import { StatusBar } from "expo-status-bar";
import type { RelayClient, RelaySnapshot } from "@relay/contracts";
import { RelayWorkbench } from "@relay/ui";
import { createWebClient, type WebClientHandle } from "./bootstrap/createWebClient";

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
  activity: [],
  gate: null,
  expansion: {
    completeSessions: 0,
    sessionTarget: 12,
    reflexesBuilt: 0,
    reflexTarget: 4,
    reviewDue: false,
  },
  recommendations: [],
  decisions: [],
  decisionLogPath: ".dev-data/dev-console/decisions.jsonl",
};

const FIXTURE_SEGMENTS = [
  {
    type: "segment.final" as const,
    atMs: 0,
    segment: {
      schemaVersion: 1 as const,
      sourceId: "fixture",
      sessionId: "session_web",
      segmentId: "seg_1",
      revision: 1,
      sequence: 1,
      startMs: 0,
      endMs: 1200,
      speakerKey: "SPEAKER_00",
      speakerConfidence: 0.92,
      text: "We should check the API before launch.",
      textConfidence: 0.95,
      final: true,
      origin: "scripted_transcript" as const,
      cursor: null,
    },
  },
  {
    type: "segment.final" as const,
    atMs: 1800,
    segment: {
      schemaVersion: 1 as const,
      sourceId: "fixture",
      sessionId: "session_web",
      segmentId: "seg_2",
      revision: 1,
      sequence: 2,
      startMs: 1800,
      endMs: 3200,
      speakerKey: "SPEAKER_01",
      speakerConfidence: 0.9,
      text: "API means Application Programming Interface in our glossary.",
      textConfidence: 0.96,
      final: true,
      origin: "scripted_transcript" as const,
      cursor: null,
    },
  },
];

export function App() {
  const handleRef = useRef<WebClientHandle | null>(null);
  const clientRef = useRef<RelayClient | null>(null);
  const [snapshot, setSnapshot] = useState<RelaySnapshot>(EMPTY_SNAPSHOT);
  const [traceLines, setTraceLines] = useState<string[]>([]);

  useEffect(() => {
    const handle = createWebClient();
    handleRef.current = handle;
    clientRef.current = handle.client;

    const unsubscribe = handle.client.subscribe((change) => {
      if (change.type === "SnapshotReplaced") {
        setSnapshot(change.snapshot);
        return;
      }
        if (change.type === "TraceAppended") {
          setTraceLines((prev) => [...prev.slice(-199), change.message]);
        }
    });

    void handle.start();

    return () => {
      unsubscribe();
      clientRef.current = null;
      handleRef.current = null;
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
        onRemember={(token) => {
          void clientRef.current?.execute({ type: "RememberToken", token });
        }}
        onStartSession={() => {
          void clientRef.current?.execute({ type: "StartWorkSession" });
        }}
        onEndSession={() => {
          void clientRef.current?.execute({ type: "EndWorkSession" });
        }}
        traceLines={traceLines}
        onReplayFixture={async (fixture, speed) => {
          const handle = handleRef.current;
          if (!handle) return;
          if (fixture !== "acronym-basic") {
            setTraceLines((prev) => [
              ...prev.slice(-199),
              `replay rejected: ${fixture} is not loaded`,
            ]);
            return;
          }
          setTraceLines((prev) => [...prev.slice(-199), `replay:${fixture}@${speed}x`]);
          await clientRef.current?.execute({ type: "SetListening", enabled: true });
          let previousAt = 0;
          for (const event of FIXTURE_SEGMENTS) {
            const gap = speed === 0 ? 0 : Math.max(0, event.atMs - previousAt) / speed;
            previousAt = event.atMs;
            if (gap > 0) {
              await new Promise((resolve) => setTimeout(resolve, gap));
            }
            await handle.engine.ingestFinalSegment(
              { ...event.segment, sessionId: "session_web" },
              false,
            );
          }
        }}
      />
      <StatusBar style="light" />
    </>
  );
}
