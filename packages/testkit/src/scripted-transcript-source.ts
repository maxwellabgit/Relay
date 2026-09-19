import { readFileSync } from "node:fs";
import type { TranscriptEvent, TranscriptSourcePort } from "@relay/contracts";

export type ScriptedClock = {
  nowMs(): number;
  sleep(ms: number, signal: AbortSignal): Promise<void>;
};

export class ScriptedTranscriptSource implements TranscriptSourcePort {
  private readonly scheduled: readonly TranscriptEvent[];

  constructor(
    private readonly fixturePath: string,
    private readonly speed: number,
    private readonly clock: ScriptedClock,
  ) {
    this.scheduled = readFixture(fixturePath);
  }

  async *events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
    if (this.scheduled.length === 0) return;
    const firstAt = this.scheduled[0]!.atMs;
    const started = this.clock.nowMs();
    for (const event of this.scheduled) {
      if (signal.aborted) return;
      if (this.speed > 0) {
        const due = started + (event.atMs - firstAt) / this.speed;
        const delay = Math.max(0, due - this.clock.nowMs());
        if (delay > 0) await this.clock.sleep(delay, signal);
      }
      if (signal.aborted) return;
      yield event;
    }
  }
}

export function readFixture(path: string): TranscriptEvent[] {
  const lines = readFileSync(path, "utf8")
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter(Boolean);
  return lines.map((line) => JSON.parse(line) as TranscriptEvent);
}

export function wallClock(): ScriptedClock {
  return {
    nowMs: () => Date.now(),
    sleep: (ms, signal) =>
      new Promise((resolve) => {
        if (signal.aborted) {
          resolve();
          return;
        }
        const t = setTimeout(resolve, ms);
        signal.addEventListener(
          "abort",
          () => {
            clearTimeout(t);
            resolve();
          },
          { once: true },
        );
      }),
  };
}

export function virtualClock(): ScriptedClock {
  let now = 0;
  return {
    nowMs: () => now,
    sleep: async (ms) => {
      now += ms;
    },
  };
}
