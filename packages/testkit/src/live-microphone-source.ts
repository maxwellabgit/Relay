import type { TranscriptEvent, TranscriptSourcePort } from "@relay/contracts";

/**
 * Live microphone source contract for adapters that own capture.
 * Engine never opens the microphone; it only receives TranscriptSegmentV1 finals.
 */
export type LiveMicrophoneSource = TranscriptSourcePort & {
  readonly kind: "microphone";
};

/** Empty stub kept for import stability in browser demos. Prefer TauriAudioPort on Windows. */
export function createLiveMicrophoneSourceStub(): LiveMicrophoneSource {
  return {
    kind: "microphone",
    events(signal: AbortSignal): AsyncIterable<TranscriptEvent> {
      void signal;
      return {
        async *[Symbol.asyncIterator]() {
          // Intentionally empty — Windows production uses Tauri audio_start/drain.
        },
      };
    },
  };
}
