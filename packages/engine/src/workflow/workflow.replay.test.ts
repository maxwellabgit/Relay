import { describe, expect, it } from "vitest";
import { MemoryFoundationStore } from "../cases/foundation-store.js";
import { WorkflowService } from "./service.js";

const listingOnly = [
  {
    observedAt: "2026-09-28T15:00:00.000Z",
    sourceType: "browser" as const,
    provider: "chrome",
    eventType: "browser.navigate",
    url: "https://www.linkedin.com/jobs/view/42",
    title: "Senior ML Engineer — Example",
  },
];

const application = [
  {
    observedAt: "2026-09-28T16:00:00.000Z",
    sourceType: "browser" as const,
    provider: "chrome",
    eventType: "browser.navigate",
    url: "https://www.linkedin.com/jobs/view/42",
    title: "Senior ML Engineer — Example",
  },
  {
    observedAt: "2026-09-28T16:06:00.000Z",
    sourceType: "desktop" as const,
    provider: "windows",
    eventType: "window.focus",
    application: "WINWORD.EXE",
    title: "master_resume.docx",
  },
  {
    observedAt: "2026-09-28T16:14:00.000Z",
    sourceType: "browser" as const,
    provider: "chrome",
    eventType: "browser.navigate",
    url: "https://jobs.example.com/apply/42",
    title: "Example application",
  },
];

describe("workflow replay", () => {
  it("replays a listing and an application episode with tools disabled", async () => {
    const service = new WorkflowService({
      records: new MemoryFoundationStore(),
      clock: { now: () => new Date("2026-09-28T16:20:00.000Z") },
      ids: (() => {
        let n = 0;
        return { next: (prefix: string) => `${prefix}_${++n}` };
      })(),
      trace: async () => undefined,
    });
    await service.execute({
      type: "Workflow",
      action: "configure",
      paused: false,
      windowsEnabled: true,
      chromeEnabled: true,
      pageContentEnabled: false,
      setupComplete: true,
      allowedSites: ["www.linkedin.com", "jobs.example.com"],
    });
    await service.execute({ type: "Workflow", action: "ingest", signals: listingOnly });
    await service.execute({ type: "Workflow", action: "ingest", signals: application });
    const view = await service.view();
    const research = view.episodes.find((item) => item.outcome === "research");
    const job = view.episodes.find((item) => item.classification === "job_application");
    expect(research?.classification).toBe("job_research");
    expect(research?.outcome).not.toBe("submitted");
    expect(job?.outcome).toBe("in_progress");
    expect(job?.evidence.map((item) => item.label)).toEqual([
      "Senior ML Engineer — Example",
      "master_resume.docx",
      "Example application",
    ]);
    expect((await service.execute({ type: "Workflow", action: "run_draft", episodeId: job!.episodeId })).summary).toBe("not_active");
  });
});
