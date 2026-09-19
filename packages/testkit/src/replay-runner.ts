import type { TranscriptEvent } from "@relay/contracts";
import type { RelayEngine } from "@relay/engine";
import {
  ScriptedTranscriptSource,
  virtualClock,
  wallClock,
} from "./scripted-transcript-source.js";

export type ReplayOptions = {
  readonly fixturePath: string;
  readonly speed: number;
  readonly engine: RelayEngine;
  readonly sessionId: string;
};

export async function runReplay(options: ReplayOptions): Promise<{
  readonly events: TranscriptEvent[];
  readonly finals: number;
}> {
  const clock = options.speed === 0 ? virtualClock() : wallClock();
  const source = new ScriptedTranscriptSource(options.fixturePath, options.speed, clock);
  const collected: TranscriptEvent[] = [];
  let finals = 0;
  const ac = new AbortController();

  for await (const event of source.events(ac.signal)) {
    collected.push(event);
    if (event.type === "segment.interim") continue;
    if (event.type === "segment.speaker_revised") continue;
    if (event.type === "segment.final") {
      finals += 1;
      await options.engine.ingestFinalSegment(
        {
          ...event.segment,
          sessionId: options.sessionId,
        },
        false,
      );
    }
  }

  return { events: collected, finals };
}
