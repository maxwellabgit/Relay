export type SpeechPhase = "off" | "starting" | "capturing" | "unavailable";

export type SpeechEvent =
  | "start"
  | "source_ready"
  | "permission_denied"
  | "interruption"
  | "route_change"
  | "background"
  | "cancel"
  | "relaunch"
  | "unavailable";

export type SpeechSuspendEvent = "interruption" | "route_change" | "background" | "cancel";

export type SpeechDecision = {
  readonly phase: SpeechPhase;
  readonly listening: boolean;
  readonly detail: string;
};

const SUSPEND = new Set<SpeechEvent>(["interruption", "route_change", "background", "cancel"]);

/**
 * Foreground speech only. Capture does not continue across suspend or relaunch.
 * `source_ready` turns listening on only from `starting`, after an explicit start.
 */
export function reduceSpeechSession(phase: SpeechPhase, event: SpeechEvent): SpeechDecision {
  if (event === "start") return { phase: "starting", listening: false, detail: "starting" };
  if (event === "source_ready") {
    if (phase !== "starting") return { phase: "off", listening: false, detail: "idle" };
    return { phase: "capturing", listening: true, detail: "capturing" };
  }
  if (event === "permission_denied") {
    return { phase: "unavailable", listening: false, detail: "permission_denied" };
  }
  if (SUSPEND.has(event)) return { phase: "off", listening: false, detail: event };
  return { phase: "unavailable", listening: false, detail: "speech_unavailable" };
}

/** A process start never resumes a previous capture. */
export function speechOnRelaunch(status: { readonly ok: boolean; readonly detail: string }): SpeechDecision {
  if (status.detail === "permission_denied") return reduceSpeechSession("off", "permission_denied");
  if (!status.ok) return reduceSpeechSession("off", "unavailable");
  return { phase: "off", listening: false, detail: status.detail || "idle" };
}

export async function applySpeechSuspend(input: {
  readonly event: SpeechSuspendEvent;
  readonly wasListening: boolean;
  readonly stopCapture: () => Promise<void>;
  readonly turnListeningOff: () => Promise<void>;
}): Promise<SpeechDecision> {
  const decision = reduceSpeechSession("capturing", input.event);
  await input.stopCapture();
  if (input.wasListening) await input.turnListeningOff();
  return decision;
}
