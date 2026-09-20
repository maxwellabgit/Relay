import type { DecisionRunView, TranscriptEvent } from "@relay/contracts";
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
  readonly captureSessionId?: string;
};

export async function runReplay(options: ReplayOptions): Promise<{
  readonly events: TranscriptEvent[];
  readonly finals: number;
  readonly decisions: readonly DecisionRunView[];
}> {
  const captureSessionId = options.captureSessionId ?? `capture_${options.sessionId}`;
  const clock = options.speed === 0 ? virtualClock() : wallClock();
  const source = new ScriptedTranscriptSource(options.fixturePath, options.speed, clock);
  const collected: TranscriptEvent[] = [];
  let finals = 0;
  const ac = new AbortController();

  await options.engine.execute({ type: "SetListening", enabled: true });

  for await (const event of source.events(ac.signal)) {
    collected.push(event);
    if (event.type === "segment.interim") continue;
    if (event.type === "segment.speaker_revised") continue;
    if (event.type === "segment.final") {
      finals += 1;
      await options.engine.ingestFinalSegment(
        {
          ...event.segment,
          segmentId: `${captureSessionId}_${event.segment.segmentId}_${finals}`,
          sessionId: options.sessionId,
        },
        false,
      );
    }
  }

  for (let i = 0; i < 200; i += 1) {
    const snap = await options.engine.getSnapshot();
    if (snap.queueDepth === 0) break;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }

  const snap = await options.engine.getSnapshot();
  const decisions = snap.decision ? [snap.decision] : [];
  return { events: collected, finals, decisions };
}
