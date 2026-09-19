import type { TranscriptEvent, TranscriptSourcePort } from "@relay/contracts";

/** Interface-only Stage 1 stub — live mic lands with native adapters. */
export type LiveMicrophoneSource = TranscriptSourcePort & {
  readonly kind: "microphone";
};

export function createLiveMicrophoneSourceStub(): LiveMicrophoneSource {
  return {
    kind: "microphone",
    events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
      void signal;
      return {
        async *[Symbol.asyncIterator]() {
          // Intentionally empty until native audio adapters land.
        },
      };
    },
  };
}
