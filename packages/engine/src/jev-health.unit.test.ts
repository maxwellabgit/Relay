import { describe, expect, it } from "vitest";
import { JevHealthTracker, projectJevHealth } from "./jev-health.js";

describe("projectJevHealth", () => {
  it("reports missing key", () => {
    expect(projectJevHealth({ secretPresent: false, hostedEnabled: false, lastOutcome: "none" })).toEqual({
      ok: false,
      detail: "missing key",
    });
  });

  it("reports hosted off when key present", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: false, lastOutcome: "none" })).toEqual({
      ok: false,
      detail: "hosted off",
    });
  });

  it("reports configured before any request", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: true, lastOutcome: "none" })).toEqual({
      ok: true,
      detail: "configured",
    });
  });

  it("reports ready only after success", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: true, lastOutcome: "success" })).toEqual({
      ok: true,
      detail: "ready",
    });
  });

  it("reports degraded after failure", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: true, lastOutcome: "failure" })).toEqual({
      ok: false,
      detail: "degraded",
    });
  });
});

describe("JevHealthTracker", () => {
  it("does not clear degraded to ready on periodic refresh without success", () => {
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state.detail).toBe("configured");
    tracker.noteFailure();
    expect(tracker.state).toEqual({ ok: false, detail: "degraded" });
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state).toEqual({ ok: false, detail: "degraded" });
    tracker.noteSuccess();
    expect(tracker.state).toEqual({ ok: true, detail: "ready" });
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state).toEqual({ ok: true, detail: "ready" });
  });

  it("drops to hosted off without clearing outcome memory for later re-enable", () => {
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    tracker.noteSuccess();
    tracker.apply({ secretPresent: true, hostedEnabled: false });
    expect(tracker.state).toEqual({ ok: false, detail: "hosted off" });
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state).toEqual({ ok: true, detail: "ready" });
  });
});
