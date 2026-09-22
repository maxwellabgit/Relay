import { describe, expect, it } from "vitest";
import { applySpeechSuspend, reduceSpeechSession, speechOnRelaunch } from "./session.js";

describe("foreground speech session", () => {
  it("listens only after an explicit start reaches a ready source", () => {
    expect(reduceSpeechSession("off", "source_ready")).toEqual({
      phase: "off",
      listening: false,
      detail: "idle",
    });
    expect(reduceSpeechSession("off", "start").phase).toBe("starting");
    expect(reduceSpeechSession("starting", "source_ready")).toEqual({
      phase: "capturing",
      listening: true,
      detail: "capturing",
    });
  });

  it("drops listening on permission denial, suspend, and relaunch", () => {
    for (const event of ["permission_denied", "interruption", "route_change", "background", "cancel"] as const) {
      const decision = reduceSpeechSession("capturing", event);
      expect(decision.listening).toBe(false);
      expect(decision.detail).toBe(event);
    }
    expect(speechOnRelaunch({ ok: false, detail: "speech_unavailable" })).toEqual({
      phase: "unavailable",
      listening: false,
      detail: "speech_unavailable",
    });
    expect(speechOnRelaunch({ ok: false, detail: "permission_denied" }).detail).toBe("permission_denied");
    expect(speechOnRelaunch({ ok: true, detail: "idle" })).toEqual({
      phase: "off",
      listening: false,
      detail: "idle",
    });
  });

  it("stops capture and turns listening off when a session was active", async () => {
    let stopped = false;
    let off = false;
    const decision = await applySpeechSuspend({
      event: "background",
      wasListening: true,
      stopCapture: async () => {
        stopped = true;
      },
      turnListeningOff: async () => {
        off = true;
      },
    });
    expect(stopped).toBe(true);
    expect(off).toBe(true);
    expect(decision.detail).toBe("background");
    off = false;
    await applySpeechSuspend({
      event: "route_change",
      wasListening: false,
      stopCapture: async () => undefined,
      turnListeningOff: async () => {
        off = true;
      },
    });
    expect(off).toBe(false);
  });
});