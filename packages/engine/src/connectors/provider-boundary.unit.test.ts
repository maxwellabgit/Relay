import { describe, expect, it } from "vitest";
import { MemoryFoundationStore } from "../cases/foundation-store.js";
import { calendarEnvelope, Pass1Foundation } from "../cases/pass1.js";
import { localDateKey } from "../cases/daily-gather.js";
import { MemoryCaseFolder } from "../cases/case-folder.js";
import { localOnlyPolicy } from "@relay/contracts";
import { SafeDrafts } from "./safe-drafts.js";
import { unsafeProviderAction } from "./provider-boundary.js";

describe("Pass 2 provider boundary", () => {
  it("refuses send and delete, and creates a new draft only after commit", async () => {
    expect(unsafeProviderAction("gmail.message.send")).toBe(true);
    expect(unsafeProviderAction("docs.document.delete")).toBe(true);
    const records = new MemoryFoundationStore();
    const calls: string[] = [];
    const drafts = new SafeDrafts(
      records,
      { next: (prefix) => `${prefix}_1` },
      () => "2026-09-25T15:00:00.000Z",
      {
        async create(action) {
          calls.push(action);
          return { providerReceiptId: "prov_1" };
        },
      },
      async (body) => `ref:${body.length}`,
    );
    const proposed = await drafts.propose("gmail.draft.create", "Hello", "Body");
    expect(proposed.ok).toBe(true);
    expect(calls).toEqual([]);
    if (!proposed.ok) return;
    const created = await drafts.commit(proposed.draftId);
    expect(created).toEqual({ ok: true, providerReceiptId: "prov_1" });
    expect(calls).toEqual(["gmail.draft.create"]);
    expect(await drafts.propose("gmail.message.send", "No", "Send")).toEqual({ ok: false, reason: "unsafe_provider_action" });
  });

  it("ingests a calendar read into the Case path and stores no message body in the gather row", async () => {
    const records = new MemoryFoundationStore();
    const saved = new Map<string, Uint8Array>();
    let n = 0;
    const foundation = new Pass1Foundation({
      records,
      folder: new MemoryCaseFolder(),
      artifacts: {
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
      },
      clock: { now: () => new Date("2026-09-25T16:00:00") },
      ids: { next: (prefix) => `${prefix}_${++n}` },
      inspectConnection: async () => ({
        healthStatus: "authority_recorded",
        observationEnabled: true,
        selectedResources: ["calendar:birthdays"],
      }),
      pullSource: async () => [
        calendarEnvelope({
          eventId: "event_read",
          externalEventId: "ext_read",
          revision: "1",
          resourceId: "calendar:birthdays",
          content: "Birthday: Ada 01-01",
          selected: true,
          at: "2026-09-25T16:00:00.000Z",
        }),
      ],
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
    await foundation.setGrant({
      grantId: "grant_birthday",
      reflexId: "reflex.rule-notice",
      reflexVersion: 1,
      connectionId: "connection_sample",
      resourceIds: ["calendar:birthdays"],
      actionId: "case.entry.append@1",
      expiresAt: "2099-01-01T00:00:00.000Z",
      maxPerHour: 4,
    });
    const gathered = await foundation.runDailyRead(new Date(2026, 8, 25, 18, 0, 0).toISOString());
    expect(gathered.ran).toBe(true);
    expect(gathered.resources).toEqual(["calendar:birthdays"]);
    const row = await records.get("source_gather", localDateKey(new Date(2026, 8, 25, 18, 0, 0)));
    expect(JSON.stringify(row?.payload).includes("Ada")).toBe(false);
    const birthdays = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    expect(birthdays?.entries.some((entry) => entry.text === "Ada 01-01")).toBe(true);
  });
});
