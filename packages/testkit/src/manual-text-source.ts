import type { TranscriptEvent, TranscriptSourcePort } from "@relay/contracts";

export class ManualTextSource implements TranscriptSourcePort {
  constructor(private readonly eventsToEmit: readonly TranscriptEvent[]) {}

  async *events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
    for (const event of this.eventsToEmit) {
      if (signal.aborted) return;
      yield event;
    }
  }
}
