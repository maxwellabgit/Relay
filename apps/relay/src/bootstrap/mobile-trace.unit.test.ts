import { describe, expect, it } from "vitest";
import type { RuntimeEventV2 } from "@relay/engine";
import { createMobileTraceSink } from "./mobile-trace.js";

function memoryFiles() {
  const stored = new Map<string, Uint8Array>();
  return {
    stored,
    async write(name: string, bytes: Uint8Array) {
      stored.set(name, bytes);
    },
    async read(name: string) {
      return stored.get(name) ?? null;
    },
  };
}

function event(sequence: number): RuntimeEventV2 {
  return {
    schemaVersion: 2,
    sequence,
    runId: "run_test",
    at: "2026-09-22T00:00:00.000Z",
    eventType: "run.started",
    stage: "run",
    status: "completed",
  };
}

describe("mobile trace sink", () => {
  it("stores events on the device file port and drops the oldest past the cap", async () => {
    const files = memoryFiles();
    const sink = createMobileTraceSink(files, "run_test");
    expect(sink.directoryLabel).toBe("trace-run_test.jsonl");
    for (let sequence = 1; sequence <= 205; sequence += 1) {
      await sink.append(event(sequence));
    }
    const read = await sink.read();
    expect(read).toHaveLength(200);
    expect(read[0]?.sequence).toBe(6);
    expect(read.at(-1)?.sequence).toBe(205);
    expect(files.stored.size).toBe(1);
  });
});
