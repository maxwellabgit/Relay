import type { RelayEngine } from "@relay/engine";
import { ACRONYM_BASIC_EVENTS } from "@relay/testkit/browser";

/**
 * Developer-console fixture player. Production entries must load this only
 * through a dynamic import behind the dev-console flag.
 */
export async function replayAcronymFixture(
  engine: Pick<RelayEngine, "ingestReplayFinalSegment">,
  sessionId: string,
  speed: number,
): Promise<void> {
  const captureId = `capture_${Date.now()}`;
  let previousAt = 0;
  let index = 0;
  for (const event of ACRONYM_BASIC_EVENTS) {
    if (event.type !== "segment.final") continue;
    index += 1;
    const gap = speed === 0 ? 0 : Math.max(0, event.atMs - previousAt) / speed;
    previousAt = event.atMs;
    if (gap > 0) await new Promise((resolve) => setTimeout(resolve, gap));
    await engine.ingestReplayFinalSegment({
      ...event.segment,
      segmentId: `${captureId}_${event.segment.segmentId}_${index}`,
      sessionId,
    });
  }
}
