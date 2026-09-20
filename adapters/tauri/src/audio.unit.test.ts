import { describe, expect, it, vi } from "vitest";
import type { TranscriptSegmentV1 } from "@relay/contracts";
import { startLiveTranscriptPump, TauriAudioPort } from "./audio.js";

describe("TauriAudioPort", () => {
  it("starts, drains finals, and stops", async () => {
    const invoke = vi.fn(async (command: string) => {
      if (command === "audio_start") return { ok: true, detail: "capturing", capturing: true };
      if (command === "audio_stop") return { ok: true, detail: "idle", capturing: false };
      if (command === "audio_status") return { ok: true, detail: "idle", capturing: false };
      if (command === "audio_drain") {
        return {
          events: [
            {
              type: "segment.final",
              segment: {
                schemaVersion: 1,
                sourceId: "mic",
                sessionId: "old",
                segmentId: "seg_1",
                revision: 1,
                sequence: 1,
                startMs: 0,
                endMs: 500,
                speakerKey: null,
                speakerConfidence: null,
                text: "hello world",
                textConfidence: null,
                final: true,
                origin: "microphone",
                cursor: null,
              },
            },
          ],
        };
      }
      throw new Error(command);
    });
    const port = new TauriAudioPort(invoke);
    await expect(port.start("session_1")).resolves.toMatchObject({ capturing: true });
    const events = await port.drain();
    expect(events).toHaveLength(1);
    await expect(port.stop()).resolves.toMatchObject({ capturing: false });
  });
});

describe("live transcript pump", () => {
  it("forwards drained finals into ingest with engine session id", async () => {
    const ingested: TranscriptSegmentV1[] = [];
    const audio = new TauriAudioPort(async (command) => {
      if (command === "audio_drain") {
        return {
          events: [
            {
              type: "segment.final",
              segment: {
                schemaVersion: 1,
                sourceId: "mic",
                sessionId: "native",
                segmentId: "seg_9",
                revision: 1,
                sequence: 9,
                startMs: 0,
                endMs: 10,
                speakerKey: null,
                speakerConfidence: null,
                text: "observed phrase",
                textConfidence: null,
                final: true,
                origin: "microphone",
                cursor: null,
              },
            },
          ],
        };
      }
      return { ok: true, detail: "idle", capturing: false };
    });
    const stop = startLiveTranscriptPump({
      audio,
      sessionId: "engine_session",
      intervalMs: 20,
      ingest: async (segment) => {
        ingested.push(segment);
      },
    });
    await new Promise((r) => setTimeout(r, 60));
    stop();
    expect(ingested[0]?.sessionId).toBe("engine_session");
    expect(ingested[0]?.text).toBe("observed phrase");
  });

  it("notifies when native audio status changes after startup", async () => {
    const statuses: Array<{ ok: boolean; detail: string; capturing: boolean }> = [];
    let tick = 0;
    const audio = new TauriAudioPort(async (command) => {
      if (command === "audio_status") {
        tick += 1;
        if (tick === 1) return { ok: true, detail: "capturing", capturing: true };
        return { ok: false, detail: "source_exited", capturing: false };
      }
      if (command === "audio_drain") return { events: [] };
      return { ok: true, detail: "idle", capturing: false };
    });
    const stop = startLiveTranscriptPump({
      audio,
      sessionId: "engine_session",
      intervalMs: 15,
      ingest: async () => undefined,
      onStatus: (status) => {
        statuses.push(status);
      },
    });
    await new Promise((r) => setTimeout(r, 80));
    stop();
    expect(statuses.some((s) => s.capturing)).toBe(true);
    expect(statuses.some((s) => s.detail === "source_exited")).toBe(true);
  });
});
