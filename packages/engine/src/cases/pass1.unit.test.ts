import { localOnlyPolicy } from "@relay/contracts";
import { describe, expect, it } from "vitest";
import { MemoryCaseFolder } from "./case-folder.js";
import { MemoryFoundationStore } from "./foundation-store.js";
import { calendarEnvelope, Pass1Foundation } from "./pass1.js";

const SENTINEL = "CALENDAR_TITLE_SENTINEL_9f3a";

class MemoryArtifacts {
  readonly saved: Uint8Array[] = [];
  async put(value: Uint8Array) {
    this.saved.push(value);
    return { artifactId: `art_${this.saved.length}`, sha256: "abc", policy: localOnlyPolicy() };
  }
  async get() {
    return new Uint8Array();
  }
  async provenance() {
    return null;
  }
}

function harness() {
  let n = 0;
  const artifacts = new MemoryArtifacts();
  const foundation = new Pass1Foundation({
    records: new MemoryFoundationStore(),
    folder: new MemoryCaseFolder(),
    artifacts,
    clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
    ids: { next: (prefix) => `${prefix}_${++n}` },
  });
  return { foundation, artifacts };
}

describe("Pass 1 case and calendar slice", () => {
  it("routes a selected birthday to Birthdays and ignores an unselected event", async () => {
    const { foundation, artifacts } = harness();
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
      expiresAt: "2026-09-24T18:00:00.000Z",
      maxPerHour: 4,
    });
    const at = "2026-09-24T12:00:00.000Z";
    const selected = await foundation.ingest(
      calendarEnvelope({
        eventId: "event_1",
        externalEventId: "ext_1",
        revision: "1",
        resourceId: "calendar:birthdays",
        content: "Birthday: Maya 03-14",
        selected: true,
        at,
      }),
      "exec_1",
    );
    expect(selected.type).toBe("action_completed");
    expect(selected.receiptIds.length).toBeGreaterThan(0);
    const again = await foundation.ingest(
      calendarEnvelope({
        eventId: "event_1b",
        externalEventId: "ext_1",
        revision: "1",
        resourceId: "calendar:birthdays",
        content: "Birthday: Maya 03-14",
        selected: true,
        at,
      }),
    );
    expect(again.type).toBe("no_action");
    const before = artifacts.saved.length;
    const hidden = await foundation.ingest(
      calendarEnvelope({
        eventId: "event_hidden",
        externalEventId: "ext_hidden",
        revision: "1",
        resourceId: "calendar:other",
        content: `Birthday: Secret 01-01 ${SENTINEL}`,
        selected: false,
        at,
      }),
    );
    expect(hidden.type).toBe("no_action");
    expect(hidden.retained).toBe(false);
    expect(artifacts.saved.length).toBe(before);
    const view = await foundation.view(at);
    expect(view.caseActivity.some((item) => item.projectCaseId === "case_birthdays")).toBe(true);
    expect(view.headsUp.some((item) => item.text.includes("Maya"))).toBe(true);
    const joined = artifacts.saved.map((bytes) => new TextDecoder().decode(bytes)).join("\n");
    expect(joined.includes(SENTINEL)).toBe(false);
  });

  it("opens one contradicted Verify item and supports dismiss plus undo", async () => {
    const { foundation } = harness();
    await foundation.ensureSeeded();
    await foundation.addEntry("case_birthdays", "Maya 03-14", 1);
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
    const result = await foundation.ingest(
      calendarEnvelope({
        eventId: "event_conflict",
        externalEventId: "ext_conflict",
        revision: "2",
        resourceId: "calendar:birthdays",
        content: "Birthday: Maya 04-01",
        selected: true,
        at: "2026-09-24T12:00:00.000Z",
      }),
    );
    expect(result.type).toBe("verification_required");
    const pending = (await foundation.view()).verifyItems.find((item) => item.disposition === "pending");
    expect(pending?.evidenceStatus).toBe("Contradicted");
    const before = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    await foundation.decideVerify(pending!.verifyId, "dismiss");
    const after = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    expect(after?.version).toBe(before?.version);
    const renamed = await foundation.rename("case_birthdays", "Birthdays desk", after!.version);
    expect(renamed.projectCaseId).toBe("case_birthdays");
    expect(renamed.intent.startsWith("Remember who")).toBe(true);
    const undone = await foundation.undo("case_birthdays");
    expect(undone.alias).toBe("Birthdays");
  });

  it("keeps wake and exact acronym distinct from ordinary mention", async () => {
    const { foundation } = harness();
    await foundation.ensureSeeded();
    const wake = await foundation.onSpeech("hey relay check this", "exec_wake");
    expect(wake.type).toBe("clarification_required");
    const ordinary = await foundation.onSpeech("the relay arrived", "exec_ordinary");
    expect(ordinary.type).toBe("no_action");
    const exact = await foundation.onSpeech("API", "exec_api");
    expect(exact.type).toBe("notification");
  });
});
