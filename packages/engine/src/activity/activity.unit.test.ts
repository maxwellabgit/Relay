import { describe, expect, it } from "vitest";
import { MemoryFoundationStore } from "../cases/foundation-store.js";
import { hostEventToCommand, validateNativeMessage } from "./native-message.js";
import { ActivityObservationService } from "./service.js";
import { readSignals } from "./signals.js";
import { nextForegroundEvent } from "./windows-foreground.js";

function service(cases: readonly { projectCaseId: string; alias: string; intent: string }[] = []) {
  let n = 0;
  const records = new MemoryFoundationStore();
  const created = new ActivityObservationService({
    clock: { now: () => new Date("2026-09-29T18:00:00.000Z") },
    ids: { next: (prefix) => `${prefix}_${++n}` },
    records,
    listCases: async () => cases,
  });
  return { service: created, records };
}

const cursor = {
  timestamp: "2026-09-29T15:00:00.000Z",
  source: { type: "windows", provider: "setwineventhook" },
  eventType: "foreground.changed",
  application: { processName: "cursor.exe", windowTitle: "relay — Cursor" },
};

describe("activity observation", () => {
  it("rejects observation while the control is off and accepts it after enabling", async () => {
    const { service: activity } = service();
    const blocked = await activity.ingest(cursor);
    expect(blocked.ok).toBe(false);
    expect(blocked.toolsInvoked).toEqual([]);
    await activity.applySettings({ enabled: true });
    const accepted = await activity.ingest(cursor);
    expect(accepted.ok).toBe(true);
    expect(activity.view().episodes[0]?.applications).toContain("Cursor");
    expect(activity.view().episodes[0]?.classification.label).toBe("development"); // pragma: allowlist secret
  });

  it("drops duplicate foreground samples", async () => {
    const { service: activity } = service();
    await activity.applySettings({ enabled: true });
    await activity.ingest(cursor);
    const duplicate = await activity.ingest({ ...cursor, timestamp: "2026-09-29T15:00:01.000Z" });
    expect(duplicate.summary).toBe("duplicate");
    expect(activity.view().episodes).toHaveLength(1);
  });

  it("splits episodes across a long gap", async () => {
    const { service: activity } = service();
    await activity.applySettings({ enabled: true });
    await activity.ingest(cursor);
    await activity.ingest({
      timestamp: "2026-09-29T15:20:00.000Z",
      source: { type: "windows", provider: "setwineventhook" },
      eventType: "foreground.changed",
      application: { processName: "explorer.exe", windowTitle: "Documents" },
    });
    expect(activity.view().episodes).toHaveLength(2);
    expect(activity.view().episodes.map((item) => item.applications.at(-1))).toEqual(["Explorer", "Cursor"]);
  });

  it("keeps one job page as research and stores a manual case correction", async () => {
    const { service: activity } = service([{ projectCaseId: "case_job", alias: "Job Search", intent: "Find a role." }]);
    await activity.applySettings({ enabled: true, chromeEnabled: true, pageContentEnabled: true });
    await activity.permitDomain("jobs.companyx.example");
    await activity.ingest({
      timestamp: "2026-09-29T10:18:00.000Z",
      source: { type: "chrome", provider: "extension" },
      eventType: "chrome.navigation",
      application: { processName: "chrome.exe" },
      resource: {
        uri: "https://jobs.companyx.example/1234",
        domain: "jobs.companyx.example",
        title: "Senior ML Engineer — Company X",
      },
      data: { heading: "Senior ML Engineer", text: "x".repeat(120) },
    });
    const episode = activity.view().episodes[0];
    expect(episode?.classification.label).toBe("job_research");
    expect(episode?.caseId).toBe("case_job");
    await activity.assign(episode?.id ?? "", "case_other", "Other");
    expect(activity.view().episodes[0]?.manualCase).toBe(true);
    expect(activity.view().episodes[0]?.caseId).toBe("case_other");
  });

  it("correlates a Chrome window title with the extension page", async () => {
    const { service: activity } = service();
    await activity.applySettings({ enabled: true, chromeEnabled: true, pageContentEnabled: true });
    await activity.permitDomain("jobs.companyx.example");
    await activity.ingest({
      timestamp: "2026-09-29T10:18:00.000Z",
      source: { type: "windows", provider: "setwineventhook" },
      eventType: "foreground.changed",
      application: { processName: "chrome.exe", windowTitle: "Senior ML Engineer — Company X - Google Chrome" },
    });
    const correlated = await activity.ingest({
      timestamp: "2026-09-29T10:18:05.000Z",
      source: { type: "chrome", provider: "extension" },
      eventType: "chrome.navigation",
      application: { processName: "chrome.exe" },
      resource: {
        uri: "https://jobs.companyx.example/1234",
        domain: "jobs.companyx.example",
        title: "Senior ML Engineer — Company X",
      },
      data: { text: "Job description ".repeat(10) },
    });
    expect(correlated.summary).toBe("observation_correlated");
    expect(activity.view().episodes).toHaveLength(1);
    expect(activity.view().trace.some((item) => item.type === "observation.correlated")).toBe(true);
  });

  it("reloads episodes from the foundation record", async () => {
    const { service: first, records } = service();
    await first.applySettings({ enabled: true });
    await first.ingest(cursor);
    const second = new ActivityObservationService({
      clock: { now: () => new Date("2026-09-29T18:00:00.000Z") },
      ids: { next: (prefix) => `${prefix}_next` },
      records,
      listCases: async () => [],
    });
    await second.load();
    expect(second.view().episodes.some((item) => item.applications.includes("Cursor"))).toBe(true);
  });

  it("does not treat a job title containing Application as an application page", () => {
    const signals = readSignals({
      id: "obs_title",
      timestamp: "2026-09-29T15:00:00.000Z",
      source: { type: "chrome", provider: "extension" },
      eventType: "chrome.navigation",
      application: { processName: "chrome.exe" },
      resource: {
        uri: "https://jobs.example.com/1",
        domain: "jobs.example.com",
        title: "Application Engineer — Acme",
      },
    });
    expect(signals.applicationPage).toBe(false);
    expect(signals.jobPosting).toBe(true);
  });

  it("dedupes foreground samples before they are emitted", () => {
    const first = nextForegroundEvent(null, { atMs: 1_000, processName: "cursor.exe", windowTitle: "relay" });
    expect(first.emit).toBe(true);
    if (!first.emit) return;
    const again = nextForegroundEvent(first.sample, { atMs: 2_000, processName: "cursor.exe", windowTitle: "relay" });
    expect(again.emit).toBe(false);
  });

  it("rejects native messages that try to invoke tools", () => {
    expect(validateNativeMessage({ type: "tool.execute", command: "shell" }, 40).ok).toBe(false);
    expect(validateNativeMessage({ type: "chrome.hello" }, 20)).toEqual({ ok: true, kind: "hello" });
    expect(hostEventToCommand({ kind: "tool.execute", command: "shell" })).toBeNull();
    expect(hostEventToCommand({ kind: "observation", observation: { eventType: "foreground.changed" } })?.type).toBe(
      "IngestActivityObservation",
    );
  });
});
