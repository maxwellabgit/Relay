import { describe, expect, it } from "vitest";
import { coreHealth, humanNotice, humanWait, isRetrying, productSurface, surfaceCopy } from "./product-surface.js";

describe("product surface", () => {
  it("shows starting before the client is live, then ready when the engine is up", () => {
    expect(
      productSurface({
        phase: "booting",
        busy: false,
        queueDepth: 0,
        waitCount: 0,
        engineOk: null,
        retrying: false,
      }),
    ).toBe("initializing");
    expect(
      productSurface({
        phase: "live",
        busy: false,
        queueDepth: 0,
        waitCount: 0,
        engineOk: true,
        retrying: false,
      }),
    ).toBe("ready");
    expect(surfaceCopy("initializing")).toBe("Starting RELAY…");
    expect(surfaceCopy("ready")).toBeNull();
  });

  it("keeps Jev or Halo off the core failure color", () => {
    expect(
      coreHealth(
        [
          { id: "engine", ok: true, label: "Engine" },
          { id: "jev", ok: false, label: "Jev" },
          { id: "halo", ok: false, label: "Halo" },
        ],
        [],
      ),
    ).toEqual({ level: "warn", label: "Jev" });
    expect(coreHealth([{ id: "engine", ok: false, label: "Engine" }], [])).toEqual({
      level: "bad",
      label: "Engine",
    });
  });

  it("names waiting, busy, and retrying work", () => {
    expect(
      productSurface({
        phase: "live",
        busy: false,
        queueDepth: 0,
        waitCount: 1,
        engineOk: true,
        retrying: false,
      }),
    ).toBe("waiting");
    expect(
      productSurface({
        phase: "live",
        busy: true,
        queueDepth: 0,
        waitCount: 0,
        engineOk: true,
        retrying: false,
      }),
    ).toBe("busy");
    expect(isRetrying([{ status: "waiting", attempt: 2 }])).toBe(true);
    expect(isRetrying([{ status: "waiting", attempt: 1 }])).toBe(false);
  });

  it("turns machine codes into short sentences", () => {
    expect(humanNotice("client_not_ready")).toBe("RELAY is still starting.");
    expect(humanNotice("speech_unavailable")).toBe("Speech is not available on this device.");
    expect(humanNotice("unknown_code")).toBe("Something went wrong. Details are in diagnostics.");
    expect(humanNotice("Listening stays off until audio is ready.")).toBe(
      "Listening stays off until audio is ready.",
    );
    expect(humanWait("hosted_judgment")).toBe("Waiting for a judgment.");
    expect(humanWait("queue_pause")).toBe("Waiting.");
  });
});
