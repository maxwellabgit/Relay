import type { TranscriptEvent, TranscriptSourcePort } from "@relay/contracts";
import { ManualTextSource } from "./manual-text-source.js";
import { RecordedAudioSource } from "./recorded-audio-source.js";
import { ScriptedTranscriptSource, type ScriptedClock, virtualClock, wallClock } from "./scripted-transcript-source.js";

/** Typed input shares the same SourceEvent / TranscriptEvent stream as fixtures. */
export class TypedInputSource implements TranscriptSourcePort {
  private readonly inner: ManualTextSource;

  constructor(events: readonly TranscriptEvent[]) {
    this.inner = new ManualTextSource(events);
  }

  events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
    return this.inner.events(signal);
  }
}

/** Transcript JSONL fixtures emit the same versioned SourceEvent path. */
export class TranscriptFixtureSource implements TranscriptSourcePort {
  private readonly inner: ScriptedTranscriptSource;

  constructor(fixturePath: string, speed: number, clock?: ScriptedClock) {
    this.inner = new ScriptedTranscriptSource(
      fixturePath,
      speed,
      clock ?? (speed === 0 ? virtualClock() : wallClock()),
    );
  }

  events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
    return this.inner.events(signal);
  }
}

/** Audio segment fixtures stream through the same SourceEvent interface. */
export class AudioFixtureSource implements TranscriptSourcePort {
  private readonly inner: RecordedAudioSource;

  constructor(segmentsPath: string, speed: number, clock?: ScriptedClock) {
    this.inner = new RecordedAudioSource(segmentsPath, speed, clock);
  }

  events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
    return this.inner.events(signal);
  }
}
