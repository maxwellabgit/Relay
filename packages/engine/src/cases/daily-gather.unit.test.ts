import { describe, expect, it } from "vitest";
import { MemoryCaseFolder } from "./case-folder.js";
import { dailyReadMinute, gatherIsDue, localDateKey } from "./daily-gather.js";
import { MemoryFoundationStore } from "./foundation-store.js";
import { Pass1Foundation } from "./pass1.js";
import { localOnlyPolicy } from "@relay/contracts";

describe("daily read-only gather", () => {
  it("picks one minute from 09:00 through 16:59 and keeps it for that date", () => {
    const first = dailyReadMinute("2026-09-25");
    const again = dailyReadMinute("2026-09-25");
    expect(again).toBe(first);
    expect(first).toBeGreaterThanOrEqual(9 * 60);
    expect(first).toBeLessThan(17 * 60);
    expect(dailyReadMinute("2026-09-26")).not.toBe(first);
  });

  it("gathers bound resource ids once after the slot and does not edit a Case", async () => {
    const records = new MemoryFoundationStore();
    const saved = new Map<string, Uint8Array>();
    let n = 0;
    const artifacts = {
      async put(value: Uint8Array) {
        const artifactId = `art_${++n}`;
        saved.set(artifactId, value);
        return { artifactId, sha256: "abc", policy: localOnlyPolicy() };
      },
      async get(ref: { artifactId: string }) {
        return saved.get(ref.artifactId) ?? new Uint8Array();
      },
      async provenance() {
        return null;
      },
    };
    const foundation = new Pass1Foundation({
      records,
      folder: new MemoryCaseFolder(),
      artifacts,
      clock: { now: () => new Date("2026-09-25T08:00:00") },
      ids: { next: (prefix) => prefix },
    });
    await foundation.ensureSeeded();
    await foundation.bind({
      bindingId: "bind_birthdays",
      connectionId: "connection_sample",
      resourceId: "calendar:birthdays",
      projectCaseIds: ["case_birthdays"],
      eventKinds: ["calendar.event"],
      contentLevel: "excerpt",
      retention: "case_entry",
      enabled: true,
      revoked: false,
      lastSyncAt: null,
      lagMs: null,
    });
    const day = localDateKey(new Date("2026-09-25T12:00:00"));
    const minute = dailyReadMinute(day);
    const early = new Date("2026-09-25T08:00:00");
    expect(gatherIsDue(early, minute)).toBe(false);
    const waiting = await foundation.runDailyRead(early.toISOString());
    expect(waiting.ran).toBe(false);
    const due = new Date("2026-09-25T12:00:00");
    due.setHours(Math.floor(minute / 60), minute % 60, 0, 0);
    const version = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays")?.version;
    const gathered = await foundation.runDailyRead(new Date(due.getTime() + 60_000).toISOString());
    expect(gathered.ran).toBe(true);
    expect(gathered.resources).toEqual(["calendar:birthdays"]);
    const again = await foundation.runDailyRead(new Date(due.getTime() + 120_000).toISOString());
    expect(again.ran).toBe(false);
    const stored = await records.get("source_gather", day);
    expect((stored?.payload as { mode?: string }).mode).toBe("read_only");
    expect((await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays")?.version).toBe(version);
  });
});
