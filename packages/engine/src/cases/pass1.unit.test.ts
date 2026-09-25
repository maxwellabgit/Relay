import { localOnlyPolicy } from "@relay/contracts";
import { describe, expect, it } from "vitest";
import { MemoryCaseFolder, SealedCaseFolder } from "./case-folder.js";
import { MemoryFoundationStore } from "./foundation-store.js";
import { calendarEnvelope, Pass1Foundation } from "./pass1.js";

const SENTINEL = "CALENDAR_TITLE_SENTINEL_9f3a";

class MemoryArtifacts {
  readonly saved: Uint8Array[] = [];
  private readonly byId = new Map<string, Uint8Array>();
  async put(value: Uint8Array) {
    const artifactId = `art_${this.saved.length + 1}`;
    this.saved.push(value);
    this.byId.set(artifactId, value);
    return { artifactId, sha256: "abc", policy: localOnlyPolicy() };
  }
  async get(ref: { artifactId: string }) {
    return this.byId.get(ref.artifactId) ?? new Uint8Array();
  }
  async provenance() {
    return null;
  }
}

function harness() {
  let n = 0;
  const artifacts = new MemoryArtifacts();
  const records = new MemoryFoundationStore();
  const folder = new MemoryCaseFolder();
  const foundation = new Pass1Foundation({
    records,
    folder,
    artifacts,
    clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
    ids: { next: (prefix) => `${prefix}_${++n}` },
    inspectConnection: async () => ({
      healthStatus: "authority_recorded",
      observationEnabled: true,
      selectedResources: ["calendar:birthdays"],
    }),
  });
  return { foundation, artifacts, records, folder };
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
    expect(pending?.acceptedText).toContain("03-14");
    expect(pending?.proposedText).toContain("04-01");
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

  it("replaces the accepted birthday when Verify is accepted", async () => {
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
    await foundation.ingest(
      calendarEnvelope({
        eventId: "event_conflict_accept",
        externalEventId: "ext_conflict_accept",
        revision: "2",
        resourceId: "calendar:birthdays",
        content: "Birthday: Maya 04-01",
        selected: true,
        at: "2026-09-24T12:00:00.000Z",
      }),
    );
    const pending = (await foundation.view()).verifyItems.find((item) => item.disposition === "pending");
    await foundation.decideVerify(pending!.verifyId, "accept");
    const birthdays = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    const dates = birthdays?.entries.filter((entry) => entry.text.startsWith("Maya")) ?? [];
    expect(dates).toHaveLength(1);
    expect(dates[0]?.text).toBe("Maya 04-01");
  });

  it("reopens a sealed Case after the folder object is replaced", async () => {
    const { records, artifacts } = harness();
    const folder = new SealedCaseFolder(artifacts, records);
    const first = new Pass1Foundation({
      records,
      folder,
      artifacts,
      clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
      ids: { next: (prefix) => `${prefix}_restart` },
      inspectConnection: async () => ({
        healthStatus: "authority_recorded",
        observationEnabled: true,
        selectedResources: ["calendar:birthdays"],
      }),
    });
    await first.ensureSeeded();
    const second = new Pass1Foundation({
      records,
      folder,
      artifacts,
      clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
      ids: { next: (prefix) => `${prefix}_again` },
    });
    await second.ensureSeeded();
    const view = await second.view();
    expect(view.projectCases.map((item) => item.projectCaseId).sort()).toEqual([
      "case_acronyms",
      "case_birthdays",
      "case_self_improvement",
    ]);
    expect(view.projectCases.find((item) => item.projectCaseId === "case_birthdays")?.intent).toContain("birthday");
    const markdown = new TextDecoder().decode((await folder.read("case_birthdays", "main.md")) ?? new Uint8Array());
    expect(markdown.startsWith("## Case Intent")).toBe(true);
    const index = await records.get("project_case", "case_birthdays");
    expect(JSON.stringify(index?.payload).includes("Remember who")).toBe(false);
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

  it("rejects an impossible date, supersedes an older proposal, and withdraws provenance", async () => {
    const { foundation, records } = harness();
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
    const invalid = await foundation.ingest(
      calendarEnvelope({
        eventId: "event_bad_date",
        externalEventId: "ext_bad",
        revision: "1",
        resourceId: "calendar:birthdays",
        content: "Birthday: Maya 02-30",
        selected: true,
        at: "2026-09-24T12:00:00.000Z",
      }),
    );
    expect(invalid.type).toBe("verification_required");
    expect(invalid.summary).toBe("Invalid date.");
    expect(invalid.receiptIds.length).toBeGreaterThan(1);
    await foundation.ingest(
      calendarEnvelope({
        eventId: "event_old",
        externalEventId: "ext_person",
        revision: "2",
        resourceId: "calendar:birthdays",
        content: "Birthday: Maya 04-01",
        selected: true,
        at: "2026-09-24T12:00:00.000Z",
      }),
    );
    await foundation.ingest(
      calendarEnvelope({
        eventId: "event_new",
        externalEventId: "ext_person",
        revision: "3",
        resourceId: "calendar:birthdays",
        content: "Birthday: Maya 05-02",
        selected: true,
        at: "2026-09-24T12:00:00.000Z",
      }),
    );
    const items = (await foundation.view()).verifyItems;
    expect(items.some((item) => item.disposition === "superseded")).toBe(true);
    expect(items.filter((item) => item.disposition === "pending" && item.proposedText.includes("Maya"))).toHaveLength(1);
    const indexed = JSON.stringify((await records.list("verify_item")).map((row) => row.payload));
    expect(indexed.includes("SENTINEL")).toBe(false);
    expect(indexed.includes("Maya")).toBe(false);
    expect(indexed.includes("subjectKey")).toBe(false);
    const caseIndex = JSON.stringify((await records.get("project_case", "case_birthdays"))?.payload);
    expect(caseIndex.includes("Maya")).toBe(false);
    expect(caseIndex.includes("subjectKey")).toBe(false);
    await foundation.bind({
      bindingId: "bind_birthdays",
      connectionId: "connection_sample",
      resourceId: "calendar:birthdays",
      projectCaseIds: ["case_birthdays"],
      eventKinds: ["calendar.event"],
      contentLevel: "metadata",
      retention: "case_entry",
      enabled: true,
      revoked: false,
      lastSyncAt: null,
      lagMs: null,
    });
    const metadata = await foundation.ingest(
      calendarEnvelope({
        eventId: "event_meta",
        externalEventId: "ext_meta",
        revision: "9",
        resourceId: "calendar:birthdays",
        content: "Birthday: Noel 01-02",
        selected: true,
        at: "2026-09-24T12:00:00.000Z",
      }),
    );
    expect(metadata.type).toBe("no_action");
    expect(metadata.summary).toContain("Metadata only");
    const birthdays = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    expect(birthdays?.entries.some((entry) => entry.text.includes("Noel"))).toBe(false);
  });

  it("does not supersede an unrelated event on the same resource", async () => {
    const { foundation } = harness();
    await foundation.ensureSeeded();
    await foundation.bind(binding());
    await foundation.ingest(birthday("event_other", "ext_other", "1", "Birthday: Meeting 01-02"));
    await foundation.ingest(birthday("event_maya", "ext_maya", "4", "Birthday: Maya 06-06"));
    const pending = (await foundation.view()).verifyItems.filter((item) => item.disposition === "pending");
    expect(pending.some((item) => item.proposedText.includes("Meeting"))).toBe(true);
    expect(pending.some((item) => item.proposedText.includes("Maya"))).toBe(true);
  });

  it("leaves the Case unchanged when the tool rejects, and returns the edit receipt when it accepts", async () => {
    const rejected = harness();
    const blocking = new Pass1Foundation({
      records: rejected.records,
      folder: rejected.folder,
      artifacts: rejected.artifacts,
      clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
      ids: { next: (prefix) => `${prefix}_block` },
      inspectConnection: async () => ({
        healthStatus: "authority_recorded",
        observationEnabled: true,
        selectedResources: ["calendar:birthdays"],
      }),
      runTool: async () => ({ ok: false, reason: "rejected" }),
    });
    await blocking.ensureSeeded();
    await blocking.bind(binding());
    await blocking.setGrant(grant());
    const before = (await blocking.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    const denied = await blocking.ingest(birthday("event_deny", "ext_deny", "1", "Birthday: Maya 03-14"), "exec_deny");
    expect(denied.type).toBe("failed");
    const after = (await blocking.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    expect(after?.version).toBe(before?.version);
    expect(after?.entries).toEqual(before?.entries);

    const allowed = harness();
    let editReceipt = "";
    let writing = allowed.foundation;
    writing = new Pass1Foundation({
      records: allowed.records,
      folder: allowed.folder,
      artifacts: allowed.artifacts,
      clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
      ids: { next: (prefix) => `${prefix}_write` },
      inspectConnection: async () => ({
        healthStatus: "authority_recorded",
        observationEnabled: true,
        selectedResources: ["calendar:birthdays"],
      }),
      runTool: (request) => writing.commitAppend(request).then((result) => {
        if (result.ok) editReceipt = result.receiptId;
        return result;
      }),
    });
    await writing.ensureSeeded();
    await writing.bind(binding());
    await writing.setGrant(grant());
    const selected = await writing.ingest(birthday("event_ok", "ext_ok", "1", "Birthday: Maya 03-14"), "exec_ok");
    expect(selected.type).toBe("action_completed");
    expect(selected.receiptIds[0]).toBe(editReceipt);
    const birthdays = (await writing.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays");
    expect(birthdays?.entries.some((entry) => entry.text === "Maya 03-14")).toBe(true);
  });

  it("reserves the receipt before a failed write and does not apply the same event twice", async () => {
    const { records, folder, artifacts } = harness();
    let n = 0;
    const foundation = new Pass1Foundation({
      records,
      folder,
      artifacts,
      clock: { now: () => new Date("2026-09-24T12:00:00.000Z") },
      ids: { next: (prefix) => `${prefix}_${++n}` },
      inspectConnection: async () => ({
        healthStatus: "authority_recorded",
        observationEnabled: true,
        selectedResources: ["calendar:birthdays"],
      }),
      runTool: async () => {
        throw new Error("interrupted");
      },
    });
    await foundation.ensureSeeded();
    await foundation.bind(binding());
    await foundation.setGrant(grant());
    const version = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays")?.version;
    await expect(foundation.ingest(birthday("event_cut", "ext_cut", "1", "Birthday: Maya 03-14"), "exec_cut")).rejects.toThrow("interrupted");
    expect((await records.get("event_receipt", "calendar.deterministic:ext_cut:1"))?.payload).toBeTruthy();
    expect((await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays")?.version).toBe(version);
    const again = await foundation.ingest(birthday("event_cut", "ext_cut", "1", "Birthday: Maya 03-14"), "exec_cut_2");
    expect(again.type).toBe("no_action");
    expect((await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays")?.entries.some((entry) => entry.text === "Maya 03-14")).toBe(false);
  });

  it("keeps one grant use when two deliveries reserve the same hour", async () => {
    const { foundation } = harness();
    await foundation.ensureSeeded();
    await foundation.bind(binding());
    await foundation.setGrant({ ...grant(), maxPerHour: 1 });
    const [first, second] = await Promise.all([
      foundation.ingest(birthday("event_q1", "ext_q1", "1", "Birthday: Ada 01-01"), "exec_q1"),
      foundation.ingest(birthday("event_q2", "ext_q2", "1", "Birthday: Grace 02-02"), "exec_q2"),
    ]);
    const completed = [first, second].filter((result) => result.type === "action_completed");
    expect(completed).toHaveLength(1);
  });

  it("does not move the cursor backward for an older revision", async () => {
    const { foundation, records } = harness();
    await foundation.ensureSeeded();
    await foundation.bind(binding());
    await foundation.setGrant(grant());
    await foundation.ingest(birthday("event_new", "ext_new", "3", "Birthday: Ada 01-01"), "exec_new");
    await foundation.ingest(birthday("event_old", "ext_old", "1", "Birthday: Grace 02-02"), "exec_old");
    const cursor = await records.get("source_cursor", "cursor:calendar.deterministic:calendar:birthdays");
    expect((cursor?.payload as { token?: string }).token).toBe("3");
    const revisions = await records.list("case_revision");
    expect(revisions.length).toBeGreaterThan(0);
    expect(revisions.every((row) => typeof (row.payload as { projectCaseId?: string }).projectCaseId === "string")).toBe(true);
  });

  it("refuses a source proposal after the binding is revoked", async () => {
    const { foundation } = harness();
    await foundation.ensureSeeded();
    await foundation.addEntry("case_birthdays", "Maya 03-14", 1);
    await foundation.bind(binding());
    await foundation.ingest(birthday("event_revoke", "ext_revoke", "2", "Birthday: Maya 04-01"));
    const pending = (await foundation.view()).verifyItems.find((item) => item.disposition === "pending");
    await foundation.bind({ ...binding(), revoked: true });
    const version = (await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays")?.version;
    await expect(foundation.decideVerify(pending!.verifyId, "accept")).rejects.toThrow("scope_revoked");
    expect((await foundation.view()).projectCases.find((item) => item.projectCaseId === "case_birthdays")?.version).toBe(version);
  });
});

function binding() {
  return {
    bindingId: "bind_birthdays",
    connectionId: "connection_sample",
    resourceId: "calendar:birthdays",
    projectCaseIds: ["case_birthdays"],
    eventKinds: ["calendar.event"],
    contentLevel: "excerpt" as const,
    retention: "case_entry" as const,
    enabled: true,
    revoked: false,
    lastSyncAt: null,
    lagMs: null,
  };
}

function grant() {
  return {
    grantId: "grant_birthday",
    reflexId: "reflex.rule-notice",
    reflexVersion: 1,
    connectionId: "connection_sample",
    resourceIds: ["calendar:birthdays"],
    actionId: "case.entry.append@1",
    expiresAt: "2026-09-24T18:00:00.000Z",
    maxPerHour: 4,
  };
}

function birthday(eventId: string, externalEventId: string, revision: string, content: string) {
  return calendarEnvelope({
    eventId,
    externalEventId,
    revision,
    resourceId: "calendar:birthdays",
    content,
    selected: true,
    at: "2026-09-24T12:00:00.000Z",
  });
}
