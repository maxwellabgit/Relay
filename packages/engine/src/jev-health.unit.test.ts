import { describe, expect, it } from "vitest";
import { JevHealthTracker, projectJevHealth } from "./jev-health.js";

describe("projectJevHealth", () => {
  it("reports unconfigured without a key", () => {
    expect(projectJevHealth({ secretPresent: false, hostedEnabled: false, liveCanary: "none" })).toEqual({
      ok: false,
      state: "unconfigured",
      detail: "missing key",
    });
  });

  it("reports disabled when hosted decisions are off", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: false, liveCanary: "none" })).toEqual({
      ok: false,
      state: "disabled",
      detail: "hosted off",
    });
  });

  it("stays configured_not_tested before a live canary", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: true, liveCanary: "none" })).toEqual({
      ok: false,
      state: "configured_not_tested",
      detail: "configured",
    });
  });

  it("reports healthy_live only after a successful live canary", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: true, liveCanary: "success" })).toEqual({
      ok: true,
      state: "healthy_live",
      detail: "ready",
    });
  });

  it("reports degraded after a failed canary and unavailable when the provider cannot be reached", () => {
    expect(projectJevHealth({ secretPresent: true, hostedEnabled: true, liveCanary: "failure" })).toEqual({
      ok: false,
      state: "degraded",
      detail: "degraded",
    });
    expect(
      projectJevHealth({
        secretPresent: true,
        hostedEnabled: true,
        liveCanary: "none",
        availability: "unreachable",
      }).state,
    ).toBe("unavailable");
  });
});

describe("JevHealthTracker", () => {
  it("does not treat an ordinary success as healthy_live", () => {
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state.state).toBe("configured_not_tested");
    tracker.noteSuccess();
    expect(tracker.state.state).toBe("configured_not_tested");
    tracker.noteLiveCanary();
    expect(tracker.state).toEqual({ ok: true, state: "healthy_live", detail: "ready" });
  });

  it("keeps configured_not_tested when only configuration apply runs", () => {
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state.state).toBe("configured_not_tested");
  });

  it("keeps healthy_live when configuration is reapplied after a canary", () => {
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    tracker.noteLiveCanary();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state.state).toBe("healthy_live");
  });

  it("marks degraded only from noteFailure, not from apply", () => {
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state.state).toBe("configured_not_tested");
    tracker.noteFailure();
    expect(tracker.state.state).toBe("degraded");
  });

  it("returns to disabled without forgetting a later re-enable", () => {
    const tracker = new JevHealthTracker();
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    tracker.noteLiveCanary();
    tracker.apply({ secretPresent: true, hostedEnabled: false });
    expect(tracker.state.state).toBe("disabled");
    tracker.apply({ secretPresent: true, hostedEnabled: true });
    expect(tracker.state.state).toBe("healthy_live");
  });
});
