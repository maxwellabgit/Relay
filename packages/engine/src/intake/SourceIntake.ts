import type { TranscriptSegmentV1 } from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import { initialDisclosureSeal } from "../disclosure/hosted-grant.js";
import { shouldCreateCaseForFinal } from "../policies.js";
import { encodeText, sha256Hex, type EngineTrace } from "../engine-helpers.js";
import type { Clock, IdFactory } from "../scheduler.js";
import type { Scheduler } from "../scheduler.js";
import type { EngineStore } from "../store.js";
import type { ArtifactStorePort } from "@relay/contracts";
import { runInTransaction } from "../transactions.js";

export type SourceIntakeDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly sessionId: string;
  readonly scheduler: Scheduler;
  readonly trace: EngineTrace;
  readonly emitSnapshot: () => Promise<void>;
};

export class SourceIntake {
  constructor(private readonly deps: SourceIntakeDeps) {}

  async ingestFinalSegment(segment: TranscriptSegmentV1, isAsk: boolean): Promise<string> {
    return this.commitFinalSegment(segment, { isAsk, requireListening: !isAsk });
  }

  async ingestReplayFinalSegment(segment: TranscriptSegmentV1): Promise<string> {
    if (segment.origin !== "scripted_transcript" && segment.origin !== "audio_file") {
      await this.deps.trace.note("source.rejected");
      await this.deps.emitSnapshot();
      return "";
    }
    return this.commitFinalSegment(segment, { isAsk: false, requireListening: false });
  }

  makeTypedSegment(text: string): TranscriptSegmentV1 {
    const now = this.deps.clock.now().getTime();
    return {
      schemaVersion: 1,
      sourceId: "manual",
      sessionId: this.deps.sessionId,
      segmentId: this.deps.ids.next("seg"),
      revision: 1,
      sequence: now,
      startMs: now,
      endMs: now,
      speakerKey: null,
      speakerConfidence: null,
      text,
      textConfidence: 1,
      final: true,
      origin: "typed",
      cursor: null,
    };
  }

  private async sealPolicy(
    segment: TranscriptSegmentV1,
    isAsk: boolean,
  ): Promise<ReturnType<typeof localOnlyPolicy>> {
    const now = this.deps.clock.now().toISOString();
    const scope = { kind: "session" as const, id: this.deps.sessionId };
    if (segment.origin === "microphone" && !isAsk) {
      return initialDisclosureSeal(this.deps.store, now, scope, "ambient_transcript");
    }
    if (isAsk || segment.origin === "typed") {
      return initialDisclosureSeal(this.deps.store, now, scope, "conversation_excerpt");
    }
    return localOnlyPolicy();
  }

  async commitFinalSegment(
    segment: TranscriptSegmentV1,
    opts: { readonly isAsk: boolean; readonly requireListening: boolean },
  ): Promise<string> {
    const bytes = encodeText(segment.text);
    const sha256 = await sha256Hex(bytes);
    const seal = await this.sealPolicy(segment, opts.isAsk);
    const artifact = await this.deps.artifacts.put(bytes, seal);
    const sourceEventId = this.deps.ids.next("src");
    const at = this.deps.clock.now().toISOString();

    const { inserted } = await this.deps.store.persistFinalSource({
      sourceEventId,
      sessionId: segment.sessionId,
      segment,
      textArtifactId: artifact.artifactId,
      textSha256: sha256,
      policy: artifact.policy,
      createdAt: at,
    });

    if (!inserted) {
      return "";
    }

    if (opts.requireListening) {
      const listening = await this.deps.store.getListening(this.deps.sessionId);
      if (!listening) {
        await this.deps.trace.note("source.rejected");
        await this.deps.emitSnapshot();
        return "";
      }
    }

    const routing = shouldCreateCaseForFinal(segment.origin, opts.isAsk);
    const caseId = this.deps.ids.next("case");
    const record = await runInTransaction(this.deps.store, async () => {
      const created = await this.deps.store.createCase({
        caseId,
        origin: routing.origin,
        kind: routing.kind,
        priority: routing.priority,
        at,
      });
      await this.deps.store.appendDomainEvent("source.case_bound", at, {
        sourceEventId,
        caseId: created.caseId,
        isAsk: opts.isAsk,
      });
      return created;
    });

    await this.deps.trace.emit({
      type: "source.accepted",
      stage: "source.accept",
      status: "completed",
      caseId: record.caseId,
      reasonCode: opts.isAsk ? "direct_answer" : "detected",
    });
    await this.deps.trace.emit({
      type: "case.created",
      stage: "case.create",
      status: "completed",
      caseId: record.caseId,
      reasonCode: opts.isAsk ? "direct_answer" : "detected",
    });

    await this.deps.scheduler.enqueue(
      "source.final",
      {
        sourceEventId,
        caseId: record.caseId,
        caseVersion: record.version,
        segmentId: segment.segmentId,
        textArtifactId: artifact.artifactId,
        textSha256: sha256,
        isAsk: opts.isAsk,
      },
      routing.priority,
      this.deps.ids,
      0,
      { correlationId: record.caseId },
    );

    await this.deps.emitSnapshot();
    return record.caseId;
  }
}
