export type SpeechStatus = {
  readonly ok: boolean;
  readonly capturing: boolean;
  readonly detail: string;
};

/**
 * V1 listening is an explicit foreground session.
 * Until a device speech adapter reports source.ready, Listening stays off.
 */
export type ForegroundSpeechPort = {
  status(): Promise<SpeechStatus>;
  start(): Promise<SpeechStatus>;
  stop(): Promise<SpeechStatus>;
};

export function createUnavailableForegroundSpeech(): ForegroundSpeechPort {
  const idle: SpeechStatus = {
    ok: false,
    capturing: false,
    detail: "speech_unavailable",
  };
  return {
    async status() {
      return idle;
    },
    async start() {
      return idle;
    },
    async stop() {
      return { ok: true, capturing: false, detail: "stopped" };
    },
  };
}

export type MobileLifecycleHandlers = {
  cancelGeneration(): void;
  stopCapture(): Promise<void>;
  checkpoint(): Promise<void>;
};

export type MobileLifecycle = {
  background(): Promise<void>;
  interruption(): Promise<void>;
  memoryWarning(): Promise<void>;
};

/** On background, interruption, or memory pressure: cancel, stop capture, checkpoint. */
export function createMobileLifecycle(handlers: MobileLifecycleHandlers): MobileLifecycle {
  const suspend = async () => {
    handlers.cancelGeneration();
    await handlers.stopCapture();
    await handlers.checkpoint();
  };
  return {
    background: suspend,
    interruption: suspend,
    memoryWarning: suspend,
  };
}
