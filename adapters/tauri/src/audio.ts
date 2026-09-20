import type { TranscriptEvent, TranscriptSegmentV1 } from "@relay/contracts";

type TauriInvoke = (command: string, args?: Record<string, unknown>) => Promise<unknown>;

export type AudioStatus = {
  readonly ok: boolean;
  readonly detail: string;
  readonly capturing: boolean;
};

/**
 * Tauri-backed live transcript intake. Native code owns the allowlisted audio process;
 * this adapter only starts/stops/drains validated finals.
 */
export class TauriAudioPort {
  constructor(private readonly invoke: TauriInvoke) {}

  async start(sessionId: string): Promise<AudioStatus> {
    const result = (await this.invoke("audio_start", {
      request: { session_id: sessionId },
    })) as AudioStatus;
    return normalizeStatus(result);
  }

  async stop(): Promise<AudioStatus> {
    const result = (await this.invoke("audio_stop")) as AudioStatus;
    return normalizeStatus(result);
  }

  async status(): Promise<AudioStatus> {
    const result = (await this.invoke("audio_status")) as AudioStatus;
    return normalizeStatus(result);
  }

  async drain(): Promise<readonly TranscriptEvent[]> {
    const result = (await this.invoke("audio_drain")) as { events?: unknown[] };
    const events = Array.isArray(result?.events) ? result.events : [];
    return events.filter(isFinalEvent);
  }
}

export type LiveTranscriptPumpOptions = {
  readonly audio: TauriAudioPort;
  readonly sessionId: string;
  readonly ingest: (segment: TranscriptSegmentV1) => Promise<void>;
  readonly intervalMs?: number;
};

/** Polls drain and forwards finals into RelayEngine.ingestFinalSegment. */
export function startLiveTranscriptPump(options: LiveTranscriptPumpOptions): () => void {
  let stopped = false;
  const intervalMs = options.intervalMs ?? 200;
  const timer = setInterval(() => {
    void tick();
  }, intervalMs);

  async function tick(): Promise<void> {
    if (stopped) return;
    try {
      const events = await options.audio.drain();
      for (const event of events) {
        if (stopped) return;
        if (event.type !== "segment.final") continue;
        await options.ingest({
          ...event.segment,
          sessionId: options.sessionId,
        });
      }
    } catch {
      // Source crash must not kill the engine loop.
    }
  }

  return () => {
    stopped = true;
    clearInterval(timer);
  };
}

function normalizeStatus(result: AudioStatus | null | undefined): AudioStatus {
  return {
    ok: result?.ok === true,
    detail: result?.detail ?? "unavailable",
    capturing: result?.capturing === true,
  };
}

function isFinalEvent(value: unknown): value is TranscriptEvent & { type: "segment.final" } {
  if (!value || typeof value !== "object") return false;
  const row = value as Record<string, unknown>;
  if (row.type !== "segment.final") return false;
  const segment = row.segment;
  if (!segment || typeof segment !== "object") return false;
  const text = (segment as { text?: unknown }).text;
  return typeof text === "string" && text.length > 0;
}
