import { describe, expect, it } from "vitest";
import { localOnlyPolicy } from "@relay/contracts";
import { MemoryEngineStore } from "./memory-store.js";

describe("MemoryEngineStore", () => {
  it("persists sessions, source events, cases, and work queue", async () => {
    const store = new MemoryEngineStore();
    const at = "2026-01-01T00:00:00.000Z";
    await store.ensureSession("session_1", at);
    await store.setListening("session_1", true);
    expect(await store.getListening("session_1")).toBe(true);

    const { inserted } = await store.persistFinalSource({
      sourceEventId: "src_1",
      sessionId: "session_1",
      segment: {
        schemaVersion: 1,
        sourceId: "manual",
        sessionId: "session_1",
        segmentId: "seg_1",
        revision: 1,
        sequence: 1,
        startMs: 0,
        endMs: 1,
        speakerKey: null,
        speakerConfidence: null,
        text: "What does API mean?",
        textConfidence: 1,
        final: true,
        origin: "typed",
        cursor: null,
      },
      textArtifactId: "art_1",
      textSha256: "abc",
      policy: localOnlyPolicy(),
      createdAt: at,
    });
    expect(inserted).toBe(true);
    expect((await store.persistFinalSource({
      sourceEventId: "src_1",
      sessionId: "session_1",
      segment: {
        schemaVersion: 1,
        sourceId: "manual",
        sessionId: "session_1",
        segmentId: "seg_1",
        revision: 1,
        sequence: 1,
        startMs: 0,
        endMs: 1,
        speakerKey: null,
        speakerConfidence: null,
        text: "dup",
        textConfidence: 1,
        final: true,
        origin: "typed",
        cursor: null,
      },
      textArtifactId: "art_1",
      textSha256: "abc",
      policy: localOnlyPolicy(),
      createdAt: at,
    })).inserted).toBe(false);

    const created = await store.createCase({
      caseId: "case_1",
      origin: "direct",
      kind: "answer",
      priority: 100,
      at,
    });
    expect(created.phase).toBe("intake");

    await store.enqueue({
      workId: "work_1",
      type: "source.final",
      priority: 100,
      availableAt: at,
      payload: { caseId: "case_1" },
      createdAt: at,
    });
    expect(await store.countWorkItems()).toBe(1);

    const claimed = await store.claimNext(at, "engine", 1000);
    expect(claimed?.workId).toBe("work_1");
    await store.complete("work_1");
    expect(await store.countWorkItems()).toBe(0);

    const segments = await store.listSourceSegments("session_1");
    expect(segments).toEqual([
      {
        segmentId: "seg_1",
        speakerKey: null,
        text: "[artifact:art_1]",
        final: true,
        origin: "typed",
        sequence: 1,
      },
    ]);

    store.close();
  });
});
