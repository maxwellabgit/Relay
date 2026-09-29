import { describe, expect, it } from "vitest";
import { MemoryFoundationStore } from "../cases/foundation-store.js";
import { ActivityObservationService } from "./service.js";

function harness() {
  let n = 0;
  let now = new Date("2026-09-29T15:30:00.000Z");
  const records = new MemoryFoundationStore();
  const service = new ActivityObservationService({
    clock: { now: () => now },
    ids: { next: (prefix) => `${prefix}_${++n}` },
    records,
    listCases: async () => [],
  });
  return {
    service,
    records,
    setNow(value: string) {
      now = new Date(value);
    },
  };
}

function windows(at: string, processName: string, windowTitle: string) {
  return {
    timestamp: at,
    source: { type: "windows", provider: "setwineventhook" },
    eventType: "foreground.changed",
    application: { processName, windowTitle },
  };
}

function page(at: string, uri: string, domain: string, title: string, text?: string) {
  return {
    timestamp: at,
    source: { type: "chrome", provider: "extension" },
    eventType: "chrome.navigation",
    application: { processName: "chrome.exe", windowTitle: title },
    resource: { uri, domain, title },
    ...(text ? { data: { text, heading: title } } : {}),
  };
}

describe("episode scenarios", () => {
  it("keeps continuous editor work in one development episode", async () => { // pragma: allowlist secret
    const { service, setNow } = harness();
    setNow("2026-09-29T15:13:00.000Z");
    await service.applySettings({ enabled: true });
    const titles = ["main.ts — Cursor", "reduce.ts — Cursor", "service.ts — Cursor", "ActivityPane.tsx — Cursor"];
    for (let index = 0; index < titles.length; index += 1) {
      const minute = String(index * 4).padStart(2, "0");
      await service.ingest(windows(`2026-09-29T15:${minute}:00.000Z`, "cursor.exe", titles[index] ?? "Cursor"));
    }
    const episodes = service.view().episodes;
    expect(episodes).toHaveLength(1);
    expect(episodes[0]?.classification.label).toBe("development"); // pragma: allowlist secret
    expect(service.view().current?.classification).toBe("Development"); // pragma: allowlist secret
  });

  it("treats casual job browsing as research, not an application", async () => {
    const { service } = harness();
    await service.applySettings({ enabled: true, chromeEnabled: true, pageContentEnabled: true });
    await service.permitDomain("jobs.example.com");
    await service.ingest(page("2026-09-29T15:00:00.000Z", "https://jobs.example.com/1", "jobs.example.com", "Designer — Example", "Job description ".repeat(12)));
    await service.ingest(page("2026-09-29T15:06:00.000Z", "https://jobs.example.com/2", "jobs.example.com", "Analyst — Example", "Qualifications and responsibilities ".repeat(8)));
    await service.ingest(windows("2026-09-29T15:10:00.000Z", "WINWORD.EXE", "resume.docx - Word"));
    const episode = service.view().episodes[0];
    expect(service.view().episodes).toHaveLength(1);
    expect(episode?.classification.label).toBe("job_research");
  });

  it("folds a 90 second interruption back into the surrounding episode", async () => {
    const { service } = harness();
    await service.applySettings({ enabled: true, chromeEnabled: true, pageContentEnabled: true });
    await service.permitDomain("jobs.example.com");
    await service.ingest(page("2026-09-29T15:00:00.000Z", "https://jobs.example.com/1", "jobs.example.com", "Designer — Example", "Job description ".repeat(12)));
    await service.ingest(windows("2026-09-29T15:04:00.000Z", "slack.exe", "Slack"));
    await service.ingest(page("2026-09-29T15:05:30.000Z", "https://jobs.example.com/1", "jobs.example.com", "Designer — Example", "Job description ".repeat(12)));
    expect(service.view().episodes).toHaveLength(1);
    expect(service.view().episodes[0]?.classification.label).toBe("job_research");
  });

  it("does not present a stale open episode as current activity", async () => {
    const { service, setNow } = harness();
    setNow("2026-09-29T15:01:00.000Z");
    await service.applySettings({ enabled: true });
    await service.ingest(windows("2026-09-29T15:00:00.000Z", "cursor.exe", "main.ts — Cursor"));
    expect(service.view().current).not.toBeNull();
    setNow("2026-09-29T16:00:00.000Z");
    expect(service.view().current).toBeNull();
    expect(service.view().episodes).toHaveLength(1);
  });

  it("stops storing observations while paused", async () => {
    const { service } = harness();
    await service.applySettings({ enabled: true });
    await service.ingest(windows("2026-09-29T15:00:00.000Z", "cursor.exe", "main.ts — Cursor"));
    await service.applySettings({ enabled: false });
    const paused = await service.ingest(windows("2026-09-29T15:02:00.000Z", "explorer.exe", "Documents"));
    expect(paused.ok).toBe(false);
    expect(service.view().episodes).toHaveLength(1);
    expect(service.view().settings.enabled).toBe(false);
  });
});
