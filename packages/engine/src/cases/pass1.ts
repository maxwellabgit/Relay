import type { ArtifactStorePort } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import type {
  CaseActivity,
  EventEnvelope,
  HeadsUpNotice,
  ObservationBinding,
  ProjectCase,
  ProjectCaseEntry,
  ProjectCaseReference,
  ProjectCaseView,
  ScopedActionGrant,
  StandardReflexResult,
  VerifyItem,
  VerifyItemView,
} from "@relay/contracts";
import { assertStandardResult } from "@relay/contracts";
import type { TraceEmitInput } from "../engine-helpers.js";
import { sha256Hex } from "../engine-helpers.js";
import type { CaseFolderPort } from "./case-folder.js";
import type { FoundationStore } from "./foundation-store.js";

const ACTIVITY_MS = 120_000;
const BIRTHDAY = /^Birthday:\s+([A-Za-z][A-Za-z .'-]{0,40})\s+(\d{2}-\d{2})$/;
const WAKE = /\bhey relay\b/i;
const ORDINARY_RELAY = /\brelay\b/i;

const SEEDS: readonly { id: string; alias: string; intent: string }[] = [
  {
    id: "case_acronyms",
    alias: "Acronyms",
    intent: "Keep the single glossary of accepted expansions.",
  },
  {
    id: "case_birthdays",
    alias: "Birthdays",
    intent: "Remember who has a birthday and compute the age only when asked.",
  },
  {
    id: "case_self_improvement",
    alias: "Self-improvement",
    intent: "Count repeated patterns and point at redacted receipts.",
  },
];

type StoredCase = ProjectCase & { readonly mainSha: string };
type StoredInvocation = {
  readonly invocationId: string;
  readonly reflexId: string;
  readonly reflexVersion: number;
  readonly resultType: string;
  readonly authority: string;
  readonly executionId: string | null;
  readonly projectCaseIds: readonly string[];
  readonly receiptIds: readonly string[];
  readonly at: string;
  readonly summary: string;
};

export type Pass1Deps = {
  readonly records: FoundationStore;
  readonly folder: CaseFolderPort;
  readonly artifacts: ArtifactStorePort;
  readonly clock: { now(): Date };
  readonly ids: { next(prefix: string): string };
  readonly trace?: (input: TraceEmitInput) => Promise<void>;
  readonly jevAvailable?: boolean;
};

export class Pass1Foundation {
  private readonly activity: CaseActivity[] = [];
  private readonly heads: HeadsUpNotice[] = [];

  constructor(private readonly deps: Pass1Deps) {}

  async ensureSeeded(): Promise<void> {
    await this.deps.folder.recover();
    const existing = await this.deps.records.list("project_case");
    if (existing.length > 0) return;
    const at = this.now();
    for (const seed of SEEDS) {
      const project = this.emptyCase(seed.id, seed.alias, seed.intent, at);
      const seeded =
        seed.id === "case_acronyms"
          ? {
              ...project,
              entries: [
                {
                  entryId: "entry_api",
                  kind: "fact" as const,
                  text: "API: Application Programming Interface",
                  updatedAt: at,
                },
              ],
            }
          : project;
      await this.persistCase(seeded, "seed", null);
      if (seed.id === "case_self_improvement") {
        await this.deps.folder.writeAtomic(
          seed.id,
          "patterns.md",
          this.encode("# Patterns\n\nNo counted patterns yet.\n"),
        );
      }
    }
  }

  async view(now = this.now()): Promise<{
    projectCases: ProjectCaseView[];
    verifyItems: VerifyItemView[];
    caseActivity: CaseActivity[];
    headsUp: HeadsUpNotice[];
    observationBindings: ObservationBinding[];
    reflexInvocations: StoredInvocation[];
  }> {
    const cases = await this.cases();
    const verify = await this.verifyItems();
    const pending = new Map<string, number>();
    for (const item of verify) {
      if (item.disposition !== "pending") continue;
      for (const id of item.projectCaseIds) pending.set(id, (pending.get(id) ?? 0) + 1);
    }
    const cutoff = Date.parse(now) - ACTIVITY_MS;
    return {
      projectCases: cases.map((item) => ({
        projectCaseId: item.projectCaseId,
        alias: item.alias,
        status: item.status,
        version: item.version,
        intent: item.intent,
        entryCount: item.entries.length,
        referenceCount: item.references.length,
        pendingVerify: pending.get(item.projectCaseId) ?? 0,
      })),
      verifyItems: verify.map(toVerifyView),
      caseActivity: this.activity.filter((item) => Date.parse(item.at) >= cutoff),
      headsUp: this.heads.filter((item) => Date.parse(item.at) >= cutoff),
      observationBindings: await this.bindings(),
      reflexInvocations: (await this.deps.records.list("reflex_invocation")).map(
        (row) => row.payload as StoredInvocation,
      ),
    };
  }

  async rename(projectCaseId: string, alias: string, expectedVersion: number): Promise<StoredCase> {
    const current = await this.requireCase(projectCaseId);
    if (current.version !== expectedVersion) throw new Error("case_version_conflict");
    const next = { ...current, alias, version: current.version + 1, updatedAt: this.now() };
    await this.persistCase(next, "rename", current);
    return next;
  }

  async editIntent(projectCaseId: string, intent: string, expectedVersion: number): Promise<StoredCase> {
    const current = await this.requireCase(projectCaseId);
    if (current.version !== expectedVersion) throw new Error("case_version_conflict");
    const next = { ...current, intent: oneIntent(intent), version: current.version + 1, updatedAt: this.now() };
    await this.persistCase(next, "intent", current);
    return next;
  }

  async addEntry(
    projectCaseId: string,
    text: string,
    expectedVersion: number,
    kind: ProjectCaseEntry["kind"] = "fact",
  ): Promise<StoredCase> {
    const current = await this.requireCase(projectCaseId);
    if (current.version !== expectedVersion) throw new Error("case_version_conflict");
    const next = this.withEntry(current, {
      entryId: this.deps.ids.next("entry"),
      kind,
      text,
      updatedAt: this.now(),
    });
    await this.persistCase(next, "entry.add", current);
    this.mark(projectCaseId, "write");
    return next;
  }

  async undo(projectCaseId: string): Promise<StoredCase> {
    const current = await this.requireCase(projectCaseId);
    const revisions = await this.deps.records.list("case_revision");
    const prior = revisions
      .map((row) => row.payload as { projectCaseId: string; before: StoredCase | null; at: string })
      .filter((row) => row.projectCaseId === projectCaseId && row.before && row.before.version < current.version)
      .sort((a, b) => (b.before?.version ?? 0) - (a.before?.version ?? 0))[0];
    if (!prior?.before) throw new Error("nothing_to_undo");
    const restored: StoredCase = {
      ...prior.before,
      version: current.version + 1,
      updatedAt: this.now(),
    };
    await this.persistCase(restored, "undo", current);
    return restored;
  }

  async linkReference(projectCaseId: string, reference: ProjectCaseReference, expectedVersion: number): Promise<StoredCase> {
    const current = await this.requireCase(projectCaseId);
    if (current.version !== expectedVersion) throw new Error("case_version_conflict");
    const next: StoredCase = {
      ...current,
      version: current.version + 1,
      updatedAt: this.now(),
      references: [...current.references.filter((item) => item.referenceId !== reference.referenceId), reference],
    };
    await this.persistCase(next, "reference.link", current);
    return next;
  }

  async bind(input: ObservationBinding): Promise<void> {
    await this.deps.records.put("binding", input.bindingId, 1, input, this.now());
  }

  async setGrant(grant: ScopedActionGrant): Promise<void> {
    await this.deps.records.put("action_grant", grant.grantId, 1, grant, this.now());
  }

  async setReflexActivation(reflexId: string, activation: "active" | "paused" | "rolled_back"): Promise<void> {
    await this.deps.records.put("reflex_activation", reflexId, 1, { reflexId, activation }, this.now());
  }

  async ingest(envelope: EventEnvelope, executionId: string | null = null): Promise<StandardReflexResult> {
    const started = Date.now();
    const binding = (await this.bindings()).find(
      (item) => item.resourceId === envelope.resourceId && item.enabled && !item.revoked,
    );
    if (!binding || !envelope.selected) {
      return this.finish(this.result({
        type: "no_action",
        summary: "Unselected source. Nothing retained.",
        executionId,
        projectCaseIds: [],
        reflexId: "reflex.source-activity",
        eventId: envelope.eventId,
        authority: "denied",
        receiptIds: [],
        retained: false,
        durationMs: Date.now() - started,
      }), envelope, null);
    }
    const prior = await this.receiptFor(envelope.dedupeKey);
    if (prior) {
      return this.result({
        type: "no_action",
        summary: "Duplicate event. One outcome kept.",
        executionId: prior.executionId,
        projectCaseIds: prior.projectCaseIds,
        reflexId: "reflex.source-activity",
        eventId: envelope.eventId,
        authority: "not_required",
        receiptIds: [prior.receiptId],
        retained: false,
        durationMs: Date.now() - started,
        invocationId: prior.reflexInvocationId ?? this.deps.ids.next("inv"),
      });
    }
    const latest = await this.latestRevision(envelope.provider, envelope.externalEventId);
    if (latest && compareRevision(envelope.revision, latest) < 0) {
      return this.finish(this.result({
        type: "no_action",
        summary: "Older revision ignored.",
        executionId,
        projectCaseIds: binding.projectCaseIds,
        reflexId: "reflex.source-activity",
        eventId: envelope.eventId,
        authority: "not_required",
        receiptIds: [],
        retained: false,
        durationMs: Date.now() - started,
      }), envelope, null);
    }
    if (envelope.status === "withdrawn") {
      const receiptId = await this.putReceipt(envelope, binding.projectCaseIds, executionId, null, "withdrawn");
      return this.finish(this.result({
        type: "no_action",
        summary: "Withdrawal recorded.",
        executionId,
        projectCaseIds: binding.projectCaseIds,
        reflexId: "reflex.source-activity",
        eventId: envelope.eventId,
        authority: "not_required",
        receiptIds: [receiptId],
        retained: false,
        durationMs: Date.now() - started,
      }), envelope, receiptId);
    }
    const activation = await this.activationOf("reflex.source-activity");
    if (activation === "paused" || activation === "rolled_back") {
      return this.finish(this.result({
        type: "no_action",
        summary: activation === "paused" ? "Reflex paused." : "Reflex rolled back.",
        executionId,
        projectCaseIds: binding.projectCaseIds,
        reflexId: "reflex.source-activity",
        eventId: envelope.eventId,
        authority: "denied",
        receiptIds: [],
        retained: false,
        durationMs: Date.now() - started,
      }), envelope, null);
    }
    const content = envelope.content ?? "";
    const artifact = content
      ? await this.deps.artifacts.put(this.encode(content), localOnlyPolicy())
      : null;
    const parsed = BIRTHDAY.exec(content.trim());
    const caseIds = [...binding.projectCaseIds];
    for (const projectCaseId of caseIds) {
      if (executionId) await this.deps.records.linkExecution(executionId, projectCaseId, this.now());
      this.mark(projectCaseId, "read", executionId ?? undefined);
    }
    if (!parsed) {
      const verify = await this.openVerify({
        evidenceStatus: "Needs evidence",
        reason: "Calendar event did not match a birthday rule.",
        proposedChange: "No Case edit.",
        projectCaseIds: caseIds,
        dedupeKey: envelope.dedupeKey,
        artifactId: artifact?.artifactId ?? null,
        envelope,
      });
      const receiptId = await this.putReceipt(envelope, caseIds, executionId, null, "processed");
      return this.finish(this.result({
        type: "verification_required",
        summary: "Needs evidence.",
        executionId,
        projectCaseIds: caseIds,
        reflexId: "reflex.source-activity",
        eventId: envelope.eventId,
        authority: "not_required",
        receiptIds: [receiptId, verify.verifyId],
        retained: true,
        durationMs: Date.now() - started,
      }), envelope, receiptId);
    }
    const person = parsed[1] ?? "";
    const monthDay = parsed[2] ?? "";
    const conflicts = await this.birthdayConflicts(caseIds, person, monthDay);
    if (conflicts.length > 0) {
      const verify = await this.openVerify({
        evidenceStatus: "Contradicted",
        reason: "Accepted birthday and the new event name different dates.",
        proposedChange: `Set ${person} to ${monthDay}.`,
        projectCaseIds: caseIds,
        dedupeKey: envelope.dedupeKey,
        artifactId: artifact?.artifactId ?? null,
        envelope,
      });
      for (const projectCaseId of caseIds) this.mark(projectCaseId, "verify", executionId ?? undefined, verify.verifyId);
      this.heads.push({
        id: verify.verifyId,
        tone: "contradiction",
        text: `Birthday conflict for ${person}.`,
        at: this.now(),
        verifyId: verify.verifyId,
        ...(caseIds[0] ? { projectCaseId: caseIds[0] } : {}),
      });
      const receiptId = await this.putReceipt(envelope, caseIds, executionId, null, "processed");
      return this.finish(this.result({
        type: "verification_required",
        summary: "Contradicted birthday.",
        executionId,
        projectCaseIds: caseIds,
        reflexId: "reflex.rule-notice",
        eventId: envelope.eventId,
        authority: "denied",
        receiptIds: [receiptId, verify.verifyId],
        retained: true,
        durationMs: Date.now() - started,
      }), envelope, receiptId);
    }
    const grant = await this.matchingGrant("reflex.rule-notice", "case.entry.append@1", envelope.resourceId);
    if (!grant) {
      const verify = await this.openVerify({
        evidenceStatus: "Proposal",
        reason: "No scoped grant for a Case write.",
        proposedChange: `Add ${person} ${monthDay}.`,
        projectCaseIds: caseIds,
        dedupeKey: `${envelope.dedupeKey}:proposal`,
        artifactId: artifact?.artifactId ?? null,
        envelope,
      });
      const receiptId = await this.putReceipt(envelope, caseIds, executionId, null, "processed");
      return this.finish(this.result({
        type: "case_change_proposed",
        summary: "Write held for review.",
        executionId,
        projectCaseIds: caseIds,
        reflexId: "reflex.rule-notice",
        eventId: envelope.eventId,
        authority: "denied",
        receiptIds: [receiptId, verify.verifyId],
        retained: true,
        durationMs: Date.now() - started,
      }), envelope, receiptId);
    }
    if (Date.parse(grant.expiresAt) < Date.parse(this.now())) {
      return this.finish(this.result({
        type: "failed",
        summary: "Grant expired.",
        executionId,
        projectCaseIds: caseIds,
        reflexId: "reflex.rule-notice",
        eventId: envelope.eventId,
        authority: "denied",
        receiptIds: [],
        retained: false,
        durationMs: Date.now() - started,
      }), envelope, null);
    }
    const targetId = caseIds.find((id) => id === "case_birthdays") ?? caseIds[0];
    if (!targetId) {
      return this.finish(this.result({
        type: "no_action",
        summary: "No Case bound.",
        executionId,
        projectCaseIds: [],
        reflexId: "reflex.rule-notice",
        eventId: envelope.eventId,
        authority: "not_required",
        receiptIds: [],
        retained: false,
        durationMs: Date.now() - started,
      }), envelope, null);
    }
    const current = await this.requireCase(targetId);
    const text = `${person} ${monthDay}`;
    if (!current.entries.some((entry) => entry.text === text)) {
      const next = this.withEntry(current, {
        entryId: this.deps.ids.next("entry"),
        kind: "fact",
        text,
        updatedAt: this.now(),
      });
      await this.persistCase(next, "scoped.append", current);
    }
    this.mark(targetId, "write", executionId ?? undefined);
    this.mark(targetId, "finding", executionId ?? undefined);
    this.heads.push({
      id: this.deps.ids.next("hud"),
      tone: "info",
      text: `Birthday noted for ${person}.`,
      at: this.now(),
      projectCaseId: targetId,
    });
    const receiptId = await this.putReceipt(envelope, caseIds, executionId, null, "processed");
    const completed = this.result({
      type: "action_completed",
      summary: "Scoped Case update recorded.",
      executionId,
      projectCaseIds: caseIds,
      reflexId: "reflex.rule-notice",
      eventId: envelope.eventId,
      authority: "allowed",
      receiptIds: [receiptId],
      retained: true,
      durationMs: Date.now() - started,
      toolIds: ["case.entry.append@1"],
    });
    return this.finish(completed, envelope, receiptId);
  }

  async decideVerify(
    verifyId: string,
    decision: "accept" | "dismiss" | "correct",
    correction?: string,
  ): Promise<VerifyItem> {
    const row = await this.deps.records.get("verify_item", verifyId);
    if (!row) throw new Error("verify_missing");
    const item = row.payload as VerifyItem;
    if (item.disposition !== "pending") return item;
    const at = this.now();
    if (decision === "dismiss") {
      const next = { ...item, disposition: "dismissed" as const, version: item.version + 1, updatedAt: at, unreadCount: 0 };
      await this.deps.records.put("verify_item", verifyId, next.version, next, at);
      return next;
    }
    const projectCaseId = item.projectCaseIds[0];
    if (!projectCaseId) throw new Error("verify_without_case");
    const current = await this.requireCase(projectCaseId);
    if (current.version !== item.caseVersion && decision === "accept") {
      throw new Error("case_version_conflict");
    }
    const text = decision === "correct" ? (correction ?? "").trim() : proposedFact(item.proposedChange);
    if (!text) throw new Error("empty_change");
    const nextCase = this.withEntry(current, {
      entryId: this.deps.ids.next("entry"),
      kind: "fact",
      text,
      updatedAt: at,
    });
    await this.persistCase(nextCase, `verify.${decision}`, current);
    const next = {
      ...item,
      disposition: "accepted" as const,
      version: item.version + 1,
      updatedAt: at,
      unreadCount: 0,
    };
    await this.deps.records.put("verify_item", verifyId, next.version, next, at);
    return next;
  }

  async onSpeech(text: string, executionId: string): Promise<StandardReflexResult> {
    const started = Date.now();
    const trimmed = text.trim();
    if (WAKE.test(trimmed)) {
      return this.speechResult({
        type: "clarification_required",
        summary: "What should I check?",
        executionId,
        reflexId: "reflex.wake-intent",
        authority: "not_required",
        durationMs: Date.now() - started,
      });
    }
    if (ORDINARY_RELAY.test(trimmed)) {
      return this.speechResult({
        type: "no_action",
        summary: "Ordinary mention. No wake.",
        executionId,
        reflexId: "reflex.wake-intent",
        authority: "not_required",
        durationMs: Date.now() - started,
      });
    }
    const token = /^[A-Z]{2,12}$/.exec(trimmed)?.[0];
    if (token) {
      const acronyms = await this.requireCase("case_acronyms");
      const senses = acronyms.entries.filter((entry) => entry.text.startsWith(`${token}:`));
      this.mark("case_acronyms", "read", executionId);
      if (senses.length === 1) {
        const expansion = senses[0]?.text.split(":").slice(1).join(":").trim() ?? "";
        this.heads.push({
          id: this.deps.ids.next("hud"),
          tone: "info",
          text: `${token}: ${expansion}`,
          at: this.now(),
          projectCaseId: "case_acronyms",
        });
        return this.speechResult({
          type: "notification",
          summary: "Exact glossary hit.",
          executionId,
          reflexId: "reflex.acronym-context",
          authority: "not_required",
          projectCaseIds: ["case_acronyms"],
          durationMs: Date.now() - started,
        });
      }
      if (senses.length > 1) {
        const deferred = this.deps.jevAvailable === false;
        return this.speechResult({
          type: deferred ? "deferred" : "clarification_required",
          summary: deferred ? "Jev unavailable. Sense not chosen." : "Which sense?",
          executionId,
          reflexId: "reflex.acronym-context",
          authority: deferred ? "deferred" : "not_required",
          projectCaseIds: ["case_acronyms"],
          durationMs: Date.now() - started,
        });
      }
    }
    return this.speechResult({
      type: "no_action",
      summary: "No matching rule.",
      executionId,
      reflexId: "reflex.wake-intent",
      authority: "not_required",
      durationMs: Date.now() - started,
    });
  }

  private async speechResult(input: {
    type: StandardReflexResult["type"];
    summary: string;
    executionId: string;
    reflexId: string;
    authority: StandardReflexResult["authority"];
    durationMs: number;
    projectCaseIds?: readonly string[];
  }): Promise<StandardReflexResult> {
    const invocationId = this.deps.ids.next("inv");
    const result = assertStandardResult({
      type: input.type,
      summary: input.summary,
      executionId: input.executionId,
      projectCaseIds: input.projectCaseIds ?? [],
      reflexId: input.reflexId,
      reflexVersion: 1,
      invocationId,
      eventId: input.executionId,
      judgmentIds: [],
      toolIds: [],
      authority: input.authority,
      receiptIds: input.type === "no_action" ? [] : [invocationId],
      durationMs: input.durationMs,
      retained: false,
    });
    await this.storeInvocation(result, this.now());
    await this.emit(result);
    return result;
  }

  private result(input: {
    type: StandardReflexResult["type"];
    summary: string;
    executionId: string | null;
    projectCaseIds: readonly string[];
    reflexId: string;
    eventId: string;
    authority: StandardReflexResult["authority"];
    receiptIds: readonly string[];
    retained: boolean;
    durationMs: number;
    invocationId?: string;
    toolIds?: readonly string[];
  }): StandardReflexResult {
    const invocationId = input.invocationId ?? this.deps.ids.next("inv");
    const receiptIds =
      input.type === "action_completed" && input.receiptIds.length === 0
        ? [invocationId]
        : input.receiptIds;
    return assertStandardResult({
      type: input.type,
      summary: input.summary,
      executionId: input.executionId,
      projectCaseIds: input.projectCaseIds,
      reflexId: input.reflexId,
      reflexVersion: 1,
      invocationId,
      eventId: input.eventId,
      judgmentIds: [],
      toolIds: input.toolIds ?? [],
      authority: input.authority,
      receiptIds,
      durationMs: input.durationMs,
      retained: input.retained,
    });
  }

  private async finish(
    result: StandardReflexResult,
    envelope: EventEnvelope,
    receiptId: string | null,
  ): Promise<StandardReflexResult> {
    if (receiptId) {
      const existing = await this.receiptFor(envelope.dedupeKey);
      if (existing && !existing.reflexInvocationId) {
        await this.deps.records.put(
          "event_receipt",
          existing.receiptId,
          2,
          { ...existing, reflexInvocationId: result.invocationId },
          this.now(),
        );
      }
    }
    await this.storeInvocation(result, envelope.observedAt);
    const checkpoint = result.retained && result.summary !== "Older revision ignored.";
    if (checkpoint) {
      await this.deps.records.put(
        "source_cursor",
        `cursor:${envelope.provider}:${envelope.resourceId}`,
        1,
        {
          cursorId: `cursor:${envelope.provider}:${envelope.resourceId}`,
          bindingId: envelope.resourceId,
          provider: envelope.provider,
          resourceId: envelope.resourceId,
          token: envelope.revision,
          updatedAt: this.now(),
        },
        this.now(),
      );
    }
    await this.emit(result);
    return result;
  }

  private async emit(result: StandardReflexResult): Promise<void> {
    await this.deps.trace?.({
      type: result.type === "no_action" ? "policy.evaluated" : "outcome.recorded",
      reasonCode:
        result.type === "no_action"
          ? "no_match"
          : result.authority === "denied"
            ? "not_authorized"
            : result.authority === "deferred"
              ? "jev_unavailable"
              : "policy_pass",
      reflexId: result.reflexId,
      ...(result.executionId ? { caseId: result.executionId, executionId: result.executionId } : {}),
      ...(result.projectCaseIds[0] ? { projectCaseId: result.projectCaseIds[0] } : {}),
      reflexInvocationId: result.invocationId,
      ...(result.receiptIds[0] ? { receiptId: safeTraceId(result.receiptIds[0]) } : {}),
      durationMs: result.durationMs,
      ...(result.toolIds[0] ? { toolId: safeTraceId(result.toolIds[0].replace(/@.*$/, "")) } : {}),
    });
  }

  private async storeInvocation(result: StandardReflexResult, at: string): Promise<void> {
    const payload: StoredInvocation = {
      invocationId: result.invocationId,
      reflexId: result.reflexId,
      reflexVersion: result.reflexVersion,
      resultType: result.type,
      authority: result.authority,
      executionId: result.executionId,
      projectCaseIds: result.projectCaseIds,
      receiptIds: result.receiptIds,
      at,
      summary: result.summary,
    };
    await this.deps.records.put("reflex_invocation", result.invocationId, 1, payload, at);
  }

  private async openVerify(input: {
    evidenceStatus: VerifyItem["evidenceStatus"];
    reason: string;
    proposedChange: string;
    projectCaseIds: readonly string[];
    dedupeKey: string;
    artifactId: string | null;
    envelope: EventEnvelope;
  }): Promise<VerifyItem> {
    const existing = (await this.verifyItems()).find((item) => item.dedupeKey === input.dedupeKey);
    const at = this.now();
    if (existing) {
      const next: VerifyItem = {
        ...existing,
        version: existing.version + 1,
        unreadCount: existing.unreadCount + 1,
        updatedAt: at,
        sources: [
          ...existing.sources,
          {
            provider: input.envelope.provider,
            externalEventId: input.envelope.externalEventId,
            revision: input.envelope.revision,
            artifactId: input.artifactId,
            at,
          },
        ],
      };
      await this.deps.records.put("verify_item", existing.verifyId, next.version, next, at);
      return next;
    }
    const projectCaseId = input.projectCaseIds[0];
    const caseVersion = projectCaseId ? (await this.requireCase(projectCaseId)).version : 0;
    const item: VerifyItem = {
      verifyId: this.deps.ids.next("verify"),
      version: 1,
      evidenceStatus: input.evidenceStatus,
      disposition: "pending",
      reason: input.reason,
      proposedChange: input.proposedChange,
      projectCaseIds: input.projectCaseIds,
      sources: [
        {
          provider: input.envelope.provider,
          externalEventId: input.envelope.externalEventId,
          revision: input.envelope.revision,
          artifactId: input.artifactId,
          at,
        },
      ],
      dedupeKey: input.dedupeKey,
      unreadCount: 1,
      caseVersion,
      createdAt: at,
      updatedAt: at,
      undoOf: null,
    };
    await this.deps.records.put("verify_item", item.verifyId, 1, item, at);
    return item;
  }

  private async birthdayConflicts(caseIds: readonly string[], person: string, monthDay: string): Promise<string[]> {
    const conflicts: string[] = [];
    for (const id of caseIds) {
      const project = await this.requireCase(id);
      for (const entry of project.entries) {
        const match = /^([A-Za-z][A-Za-z .'-]{0,40})\s+(\d{2}-\d{2})$/.exec(entry.text);
        if (match && match[1] === person && match[2] !== monthDay) conflicts.push(entry.entryId);
      }
    }
    return conflicts;
  }

  private async matchingGrant(
    reflexId: string,
    actionId: string,
    resourceId: string,
  ): Promise<ScopedActionGrant | null> {
    const grants = await this.deps.records.list("action_grant");
    for (const row of grants) {
      const grant = row.payload as ScopedActionGrant;
      if (grant.reflexId !== reflexId || grant.actionId !== actionId) continue;
      if (!grant.resourceIds.includes(resourceId) && !grant.resourceIds.includes("*")) continue;
      return grant;
    }
    return null;
  }

  private async activationOf(reflexId: string): Promise<"active" | "paused" | "rolled_back"> {
    const row = await this.deps.records.get("reflex_activation", reflexId);
    if (!row) return "active";
    return (row.payload as { activation: "active" | "paused" | "rolled_back" }).activation;
  }

  private async putReceipt(
    envelope: EventEnvelope,
    projectCaseIds: readonly string[],
    executionId: string | null,
    reflexInvocationId: string | null,
    status: "processed" | "ignored" | "withdrawn",
  ): Promise<string> {
    const receiptId = this.deps.ids.next("rcpt");
    await this.deps.records.put(
      "event_receipt",
      receiptId,
      1,
      {
        receiptId,
        dedupeKey: envelope.dedupeKey,
        provider: envelope.provider,
        externalEventId: envelope.externalEventId,
        revision: envelope.revision,
        status,
        projectCaseIds,
        executionId,
        reflexInvocationId,
        createdAt: this.now(),
      },
      this.now(),
    );
    return receiptId;
  }

  private async receiptFor(dedupeKey: string): Promise<{
    receiptId: string;
    projectCaseIds: readonly string[];
    executionId: string | null;
    reflexInvocationId: string | null;
  } | null> {
    const rows = await this.deps.records.list("event_receipt");
    for (const row of rows) {
      const payload = row.payload as { dedupeKey: string; receiptId: string; projectCaseIds: readonly string[]; executionId: string | null; reflexInvocationId: string | null };
      if (payload.dedupeKey === dedupeKey) return payload;
    }
    return null;
  }

  private async latestRevision(provider: string, externalEventId: string): Promise<string | null> {
    const rows = await this.deps.records.list("event_receipt");
    let best: string | null = null;
    for (const row of rows) {
      const payload = row.payload as { provider: string; externalEventId: string; revision: string };
      if (payload.provider !== provider || payload.externalEventId !== externalEventId) continue;
      if (!best || compareRevision(payload.revision, best) > 0) best = payload.revision;
    }
    return best;
  }

  private async persistCase(next: StoredCase, op: string, before: StoredCase | null): Promise<void> {
    const at = next.updatedAt;
    const main = renderMain(next);
    const bytes = this.encode(main);
    const sha = await sha256Hex(bytes);
    const stored: StoredCase = { ...next, mainSha: sha };
    await this.deps.folder.writeAtomic(next.projectCaseId, "main.md", bytes);
    await this.deps.folder.writeAtomic(next.projectCaseId, "rules.json", this.encode(JSON.stringify(next.rules)));
    await this.deps.folder.writeAtomic(
      next.projectCaseId,
      "references.json",
      this.encode(JSON.stringify(next.references)),
    );
    await this.deps.artifacts.put(bytes, localOnlyPolicy());
    await this.deps.records.put("project_case", next.projectCaseId, stored.version, stored, at);
    await this.deps.records.put(
      "case_revision",
      this.deps.ids.next("rev"),
      stored.version,
      { projectCaseId: next.projectCaseId, op, before, at },
      at,
    );
  }

  private withEntry(current: StoredCase, entry: ProjectCaseEntry): StoredCase {
    return {
      ...current,
      version: current.version + 1,
      updatedAt: entry.updatedAt,
      entries: [...current.entries.filter((item) => item.entryId !== entry.entryId), entry],
    };
  }

  private emptyCase(id: string, alias: string, intent: string, at: string): StoredCase {
    return {
      projectCaseId: id,
      alias,
      status: "active",
      version: 1,
      intent,
      entries: [],
      references: [],
      rules: [
        {
          ruleId: "birthday.notice",
          version: 1,
          config: { match: "Birthday: <name> <MM-dd>", write: "case.entry.append@1" },
        },
      ],
      createdAt: at,
      updatedAt: at,
      mainSha: "",
    };
  }

  private async cases(): Promise<StoredCase[]> {
    return (await this.deps.records.list("project_case")).map((row) => row.payload as StoredCase);
  }

  private async requireCase(projectCaseId: string): Promise<StoredCase> {
    const row = await this.deps.records.get("project_case", projectCaseId);
    if (!row) throw new Error("case_missing");
    return row.payload as StoredCase;
  }

  private async verifyItems(): Promise<VerifyItem[]> {
    return (await this.deps.records.list("verify_item")).map((row) => row.payload as VerifyItem);
  }

  private async bindings(): Promise<ObservationBinding[]> {
    return (await this.deps.records.list("binding")).map((row) => row.payload as ObservationBinding);
  }

  private mark(
    projectCaseId: string,
    kind: CaseActivity["kind"],
    executionId?: string,
    verifyId?: string,
  ): void {
    this.activity.push({
      projectCaseId,
      kind,
      at: this.now(),
      ...(executionId ? { executionId } : {}),
      ...(verifyId ? { verifyId } : {}),
    });
  }

  private now(): string {
    return this.deps.clock.now().toISOString();
  }

  private encode(text: string): Uint8Array {
    return new TextEncoder().encode(text);
  }
}

function renderMain(project: StoredCase): string {
  const lines = ["## Case Intent", "", project.intent, "", "## Accepted", ""];
  if (project.entries.length === 0) lines.push("_No accepted entries._");
  for (const entry of project.entries) lines.push(`- ${entry.text}`);
  lines.push("");
  return lines.join("\n");
}

function oneIntent(intent: string): string {
  const trimmed = intent.trim().replace(/\s+/g, " ");
  const sentences = trimmed.split(/(?<=[.!?])\s+/).slice(0, 2);
  return sentences.join(" ");
}

function proposedFact(proposedChange: string): string {
  const match = /^Add ([A-Za-z][A-Za-z .'-]{0,40}) (\d{2}-\d{2})\.$/.exec(proposedChange)
    ?? /^Set ([A-Za-z][A-Za-z .'-]{0,40}) to (\d{2}-\d{2})\.$/.exec(proposedChange);
  if (!match) return "";
  return `${match[1]} ${match[2]}`;
}

function toVerifyView(item: VerifyItem): VerifyItemView {
  return {
    verifyId: item.verifyId,
    version: item.version,
    evidenceStatus: item.evidenceStatus,
    disposition: item.disposition,
    reason: item.reason,
    proposedChange: item.proposedChange,
    projectCaseIds: item.projectCaseIds,
    unreadCount: item.unreadCount,
    updatedAt: item.updatedAt,
  };
}

function safeTraceId(value: string): string {
  const cleaned = value.replace(/[^a-z0-9._-]/gi, "").toLowerCase();
  return cleaned.startsWith("a") || /^[a-z]/.test(cleaned) ? cleaned.slice(0, 80) : `id_${cleaned}`.slice(0, 80);
}

function compareRevision(left: string, right: string): number {
  const a = Number(left);
  const b = Number(right);
  if (Number.isFinite(a) && Number.isFinite(b)) return a - b;
  return left < right ? -1 : left > right ? 1 : 0;
}

export function calendarEnvelope(input: {
  eventId: string;
  externalEventId: string;
  revision: string;
  resourceId: string;
  content: string | null;
  selected: boolean;
  at: string;
  status?: "active" | "withdrawn";
}): EventEnvelope {
  return {
    schemaVersion: 1,
    eventId: input.eventId,
    origin: "connected",
    provider: "calendar.deterministic",
    sourceId: "calendar",
    resourceId: input.resourceId,
    externalEventId: input.externalEventId,
    revision: input.revision,
    observedAt: input.at,
    occurredAt: input.at,
    dedupeKey: `calendar.deterministic:${input.externalEventId}:${input.revision}`,
    contentRef: null,
    content: input.content,
    selected: input.selected,
    status: input.status ?? "active",
  };
}
