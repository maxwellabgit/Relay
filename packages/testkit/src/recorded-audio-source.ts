import type { TranscriptEvent, TranscriptSourcePort } from "@relay/contracts";
import { readFixture, type ScriptedClock, virtualClock, wallClock } from "./scripted-transcript-source.js";

/**
 * Streams WhisperX-prepared *.segments.jsonl through the same schedule semantics
 * as scripted transcript fixtures.
 */
export class RecordedAudioSource implements TranscriptSourcePort {
  private readonly inner: TranscriptSourcePort;

  constructor(segmentsPath: string, speed: number, clock?: ScriptedClock) {
    const resolved =
      clock ?? (speed === 0 ? virtualClock() : wallClock());
    // Reuse scripted scheduler; contents are identical at every speed.
    const events = readFixture(segmentsPath);
    this.inner = {
      async *events(signal: AbortSignal) {
        if (events.length === 0) return;
        const firstAt = events[0]!.atMs;
        const started = resolved.nowMs();
        for (const event of events) {
          if (signal.aborted) return;
          if (speed > 0) {
            const due = started + (event.atMs - firstAt) / speed;
            const delay = Math.max(0, due - resolved.nowMs());
            if (delay > 0) await resolved.sleep(delay, signal);
          }
          if (signal.aborted) return;
          yield event;
        }
      },
    };
  }

  events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
    return this.inner.events(signal);
  }
}
