import { localOnlyPolicy } from "@relay/contracts";
import type { ReceiptRecord } from "../learning-store.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import type { ArtifactStorePort } from "@relay/contracts";
import { encodeText } from "../engine-helpers.js";
import { runInTransaction } from "../transactions.js";
import type { OverlayState } from "../projections/OverlayState.js";

export type OutcomeRecorderDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly overlays: OverlayState;
};

export class OutcomeRecorder {
  constructor(private readonly deps: OutcomeRecorderDeps) {}

  async putReceipt(
    input: Omit<
      ReceiptRecord,
      | "receiptId"
      | "retries"
      | "createdAt"
      | "decisionId"
      | "optionLabels"
      | "selectedOptionId"
      | "judgmentId"
      | "reflexId"
      | "requestedAt"
      | "completedAt"
    > & {
      retries?: number;
      decisionId?: string;
      optionLabels?: Readonly<Record<string, string>>;
      selectedOptionId?: string | null;
      judgmentId?: string | null;
      reflexId?: string | null;
      requestedAt?: string | null;
      completedAt?: string | null;
    },
  ): Promise<void> {
    const now = this.deps.clock.now().toISOString();
    const receiptId = this.deps.ids.next("receipt");
    await this.deps.store.learning.putReceipt({
      caseId: input.caseId,
      gateId: input.gateId,
      policyVersion: input.policyVersion,
      questionType: input.questionType,
      provider: input.provider,
      probabilities: input.probabilities,
      thresholds: input.thresholds,
      selectedOption: input.selectedOption,
      result: input.result,
      reasonCode: input.reasonCode,
      latencyMs: input.latencyMs,
      receiptId,
      decisionId: input.decisionId ?? receiptId,
      judgmentId: input.judgmentId ?? null,
      reflexId: input.reflexId ?? null,
      optionLabels: input.optionLabels ?? {},
      selectedOptionId: input.selectedOptionId ?? input.selectedOption,
      requestedAt: input.requestedAt ?? now,
      completedAt: input.completedAt ?? (input.result === "wait" ? null : now),
      retries: input.retries ?? 0,
      createdAt: now,
    });
  }

  async publishFeedItem(item: {
    readonly itemId: string;
    readonly kind: string;
    readonly summary: string;
    readonly createdAt: string;
    readonly caseId?: string;
  }): Promise<void> {
    const bytes = encodeText(item.summary);
    const ref = await this.deps.artifacts.put(bytes, localOnlyPolicy());
    await this.deps.store.addFeedItem({
      itemId: item.itemId,
      kind: item.kind,
      contentArtifactId: ref.artifactId,
      contentSha256: ref.sha256,
      createdAt: item.createdAt,
      ...(item.caseId ? { caseId: item.caseId } : {}),
    });
  }

  async finishCase(
    caseId: string,
    version: number,
    status: "completed" | "blocked" | "failed",
    activeCaseId: string | null,
  ): Promise<void> {
    const current = await this.deps.store.getCase(caseId);
    if (!current) return;
    if (current.status === "completed" || current.status === "cancelled") return;
    const expectedVersion = current.version;
    const updated = await runInTransaction(this.deps.store, async () => {
      const next = await this.deps.store.updateCase(caseId, expectedVersion, {
        status,
        phase: status === "completed" ? "done" : "judge",
        waitKind: null,
        at: this.deps.clock.now().toISOString(),
      });
      if (!next) return null;
      await this.deps.store.appendDomainEvent("case.finished", this.deps.clock.now().toISOString(), {
        caseId,
        status,
        expectedVersion: version,
      });
      return next;
    });
    if (!updated) {
      // Retry once against the latest version — tool route may have raced a phase bump.
      const latest = await this.deps.store.getCase(caseId);
      if (!latest || latest.status === "completed" || latest.status === "cancelled") return;
      await this.deps.store.updateCase(caseId, latest.version, {
        status,
        phase: status === "completed" ? "done" : "judge",
        waitKind: null,
        at: this.deps.clock.now().toISOString(),
      });
    }
    if (activeCaseId === caseId) {
      await this.deps.overlays.clearInputPreview(caseId);
    }
  }
}
