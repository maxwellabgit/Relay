import type {
  ArtifactStorePort,
  GlassesDisplayPort,
  GitHubReadPort,
  JudgmentPort,
  PublicSearchPort,
  ReflexModule,
  RelayChange,
  RelayCommand,
  RelayCommandResult,
  RelaySnapshot,
  TextModelPort,
  TranscriptSegmentV1,
} from "@relay/contracts";
import { localOnlyPolicy } from "@relay/contracts";
import type { EpisodeDefinition } from "./episodes.js";
import { RuntimeRecorder } from "./diagnostics/RuntimeRecorder.js";
import { EngineTrace, resolveRunId } from "./engine-helpers.js";
import { AmbientTriage } from "./ambient/AmbientTriage.js";
import { buildHostedJudgmentGrant, grantAccountFor, HostedGrantLedger, sessionDisclosureView } from "./disclosure/hosted-grant.js";
import { listHostedWaits, markHostedWaitResumed } from "./judgments/durable-wait.js";
import { SourceIntake } from "./intake/SourceIntake.js";
import { JudgmentService } from "./judgments/JudgmentService.js";
import { PatternService } from "./learning/PatternService.js";
import { AuthorityState } from "./operations/AuthorityState.js";
import { OperationService } from "./operations/OperationService.js";
import { OutcomeRecorder } from "./outcomes/OutcomeRecorder.js";
import { OverlayState } from "./projections/OverlayState.js";
import { SnapshotProjector } from "./projections/SnapshotProjector.js";
import { PRIORITY_DIRECT, PRIORITY_OBSERVED } from "./queue.js";
import { CaseRuntime } from "./runtime/CaseRuntime.js";
import { WorkDispatcher } from "./runtime/WorkDispatcher.js";
import { WorkSignal } from "./runtime/work-signal.js";
import { Scheduler, type Clock, type IdFactory } from "./scheduler.js";
import type { EngineStore } from "./store.js";
import { registerBuiltinTools } from "./tools/builtins.js";
import { ToolBroker } from "./tools/ToolBroker.js";
import { ToolRegistry } from "./tools/ToolRegistry.js";
import type { TraceSink } from "./trace-sink.js";

export type EngineDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly judgments: JudgmentPort;
  readonly model: TextModelPort;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly sessionId: string;
  readonly reflexModules?: readonly ReflexModule[];
  readonly episodeDefinitions?: readonly EpisodeDefinition[];
  readonly glasses?: GlassesDisplayPort;
  readonly publicSearch?: PublicSearchPort;
  readonly github?: GitHubReadPort;
  readonly storageDetail?: string;
  readonly jevStatus?: { ok: boolean; detail: string };
  readonly modelStatus?: { ok: boolean; detail: string; model?: string | null };
  readonly audioStatus?: { ok: boolean; detail: string };
  readonly mode?: "live" | "recorded" | "replay";
  readonly gitCommit?: string;
  readonly trace?: TraceSink;
};

/**
 * Public engine facade. Owns lifecycle cancellation and delegates domain work
 * to cohesive services under intake / runtime / judgments / tools / operations / outcomes / learning / projections.
 */
export class RelayEngine {
  private readonly workSignal = new WorkSignal();
  private readonly scheduler: Scheduler;
  private readonly listeners = new Set<(change: RelayChange) => void>();
  private running = false;
  private loopPromise: Promise<void> | null = null;
  private abort: AbortController | null = null;
  private activeEpisodeId: string | null = null;
  private activeCaseId: string | null = null;

  private readonly recorder: RuntimeRecorder;
  private readonly trace: EngineTrace;
  private readonly overlays: OverlayState;
  private readonly outcomes: OutcomeRecorder;
  private readonly patterns: PatternService;
  private readonly intake: SourceIntake;
  private readonly cases: CaseRuntime;
  private readonly ambient: AmbientTriage;
  private readonly judgments: JudgmentService;
  private readonly authority: AuthorityState;
  private readonly operations: OperationService;
  private readonly tools: ToolBroker;
  private readonly dispatcher: WorkDispatcher;
  private readonly projector: SnapshotProjector;

  constructor(private readonly deps: EngineDeps) {
    this.scheduler = new Scheduler(deps.store, deps.clock, "engine", 30_000, this.workSignal);
    this.recorder = new RuntimeRecorder({
      ...(deps.trace ? { sink: deps.trace } : {}),
      clock: deps.clock,
      runId: resolveRunId(deps.trace?.runId),
    });
    this.trace = new EngineTrace(this.recorder, deps.store);
    this.overlays = new OverlayState(
      deps.store,
      deps.clock,
      deps.sessionId,
      async (text) => {
        const ref = await deps.artifacts.put(new TextEncoder().encode(text), localOnlyPolicy());
        return { artifactId: ref.artifactId, sha256: ref.sha256 };
      },
      async (artifactId, sha256) => {
        const raw = await deps.artifacts.get({ artifactId, sha256, policy: localOnlyPolicy() });
        return new TextDecoder().decode(raw);
      },
    );
    this.outcomes = new OutcomeRecorder({
      store: deps.store,
      artifacts: deps.artifacts,
      clock: deps.clock,
      ids: deps.ids,
      overlays: this.overlays,
    });

    const emitSnapshot = () => this.projector.emitSnapshot();
    const getAbortSignal = () => this.abort?.signal ?? new AbortController().signal;

    this.authority = new AuthorityState(deps.store);
    this.patterns = new PatternService({
      store: deps.store,
      artifacts: deps.artifacts,
      judgments: deps.judgments,
      clock: deps.clock,
      ids: deps.ids,
      ...(deps.mode ? { mode: deps.mode } : {}),
      ...(deps.gitCommit ? { gitCommit: deps.gitCommit } : {}),
      ...(deps.storageDetail ? { storageDetail: deps.storageDetail } : {}),
      ...(deps.episodeDefinitions ? { episodeDefinitions: deps.episodeDefinitions } : {}),
      outcomes: this.outcomes,
      trace: this.trace,
      sessionId: deps.sessionId,
      getAbortSignal,
      setActiveEpisodeId: (id) => {
        this.activeEpisodeId = id;
      },
      emitSnapshot,
      runId: () => resolveRunId(this.deps.trace?.runId),
      authority: this.authority,
    });

    this.intake = new SourceIntake({
      store: deps.store,
      artifacts: deps.artifacts,
      clock: deps.clock,
      ids: deps.ids,
      sessionId: deps.sessionId,
      scheduler: this.scheduler,
      trace: this.trace,
      emitSnapshot,
    });

    this.ambient = new AmbientTriage({
      store: deps.store,
      artifacts: deps.artifacts,
      judgments: deps.judgments,
      model: deps.model,
      clock: deps.clock,
      ids: deps.ids,
      outcomes: this.outcomes,
      overlays: this.overlays,
      scheduler: this.scheduler,
      trace: this.trace,
      getAbortSignal,
      getActiveCaseId: () => this.activeCaseId,
      emitSnapshot,
      ...(deps.mode ? { mode: deps.mode } : {}),
      sessionId: deps.sessionId,
    });

    this.cases = new CaseRuntime({
      store: deps.store,
      artifacts: deps.artifacts,
      model: deps.model,
      clock: deps.clock,
      ids: deps.ids,
      sessionId: deps.sessionId,
      ...(deps.reflexModules ? { reflexModules: deps.reflexModules } : {}),
      scheduler: this.scheduler,
      outcomes: this.outcomes,
      overlays: this.overlays,
      patterns: this.patterns,
      ambient: this.ambient,
      trace: this.trace,
      getAbortSignal,
      getActiveCaseId: () => this.activeCaseId,
      emitSnapshot,
    });

    this.judgments = new JudgmentService({
      store: deps.store,
      artifacts: deps.artifacts,
      judgments: deps.judgments,
      clock: deps.clock,
      ids: deps.ids,
      ...(deps.mode ? { mode: deps.mode } : {}),
      outcomes: this.outcomes,
      overlays: this.overlays,
      patterns: this.patterns,
      trace: this.trace,
      getAbortSignal,
      getActiveCaseId: () => this.activeCaseId,
      offerGlossary: (token, expansion) => this.cases.offerGlossary(token, expansion),
      sessionId: deps.sessionId,
    });

    this.operations = new OperationService({
      authority: this.authority,
      clock: deps.clock,
      ids: deps.ids,
      trace: this.trace,
      emitSnapshot,
      patterns: this.patterns,
    });

    const registry = new ToolRegistry();
    registerBuiltinTools(registry, {
      learning: deps.store.learning,
      artifacts: deps.artifacts,
      model: deps.model,
      clock: deps.clock,
      ...(deps.publicSearch ? { publicSearch: deps.publicSearch } : {}),
      ...(deps.github ? { github: deps.github } : {}),
    });
    this.tools = new ToolBroker({
      registry,
      store: deps.store,
      artifacts: deps.artifacts,
      model: deps.model,
      judgments: deps.judgments,
      authority: this.authority,
      clock: deps.clock,
      ids: deps.ids,
      scheduler: this.scheduler,
      outcomes: this.outcomes,
      trace: this.trace,
      getAbortSignal,
      getActiveCaseId: () => this.activeCaseId,
      emitSnapshot,
      ...(deps.mode ? { mode: deps.mode } : {}),
      ...(deps.publicSearch ? { publicSearch: deps.publicSearch } : {}),
      ...(deps.github ? { github: deps.github } : {}),
      sessionId: deps.sessionId,
    });

    this.dispatcher = new WorkDispatcher({
      store: deps.store,
      clock: deps.clock,
      ids: deps.ids,
      scheduler: this.scheduler,
      cases: this.cases,
      judgments: this.judgments,
      tools: this.tools,
      trace: this.trace,
      emitSnapshot,
      setActiveCaseId: (id) => {
        this.activeCaseId = id;
      },
      setActiveEpisodeId: (id) => {
        this.activeEpisodeId = id;
      },
      isRunning: () => this.running,
      wake: this.workSignal,
    });

    this.projector = new SnapshotProjector({
      store: deps.store,
      artifacts: deps.artifacts,
      sessionId: deps.sessionId,
      overlays: this.overlays,
      authority: this.authority,
      trace: this.trace,
      ...(deps.trace ? { traceSink: deps.trace } : {}),
      ...(deps.storageDetail ? { storageDetail: deps.storageDetail } : {}),
      ...(deps.jevStatus ? { jevStatus: deps.jevStatus } : {}),
      ...(deps.modelStatus ? { modelStatus: deps.modelStatus } : {}),
      ...(deps.audioStatus ? { audioStatus: deps.audioStatus } : {}),
      ...(deps.mode ? { mode: deps.mode } : {}),
      ...(deps.gitCommit ? { gitCommit: deps.gitCommit } : {}),
      now: () => deps.clock.now().toISOString(),
      getRunning: () => this.running,
      getActiveCaseId: () => this.activeCaseId,
      getActiveEpisodeId: () => this.activeEpisodeId,
      runId: () => resolveRunId(this.deps.trace?.runId),
      emit: (change) => this.emit(change),
    });
  }

  async start(): Promise<void> {
    if (this.running) return;
    this.running = true;
    this.abort = new AbortController();
    const at = this.deps.clock.now().toISOString();
    await this.deps.store.ensureSession(this.deps.sessionId, at);
    await this.deps.store.setListening(this.deps.sessionId, false);
    await this.deps.store.learning.compact(at);
    if (this.deps.trace) {
      this.recorder.hydrate([...(await this.deps.trace.read())]);
    } else {
      this.recorder.markSinkMissing();
    }
    this.loopPromise = this.dispatcher.runLoop(this.abort.signal);
    await this.trace.emit({ type: "run.started", reasonCode: "start" });
    await this.projector.emitSnapshot();
  }

  async stop(): Promise<void> {
    this.running = false;
    this.abort?.abort();
    await this.loopPromise;
    this.loopPromise = null;
    await this.trace.emit({ type: "run.ended", reasonCode: "completed" });
  }

  /** Force a SnapshotReplaced projection after host-side status changes (e.g. audio). */
  async refreshSnapshot(): Promise<void> {
    await this.projector.emitSnapshot();
  }

  subscribe(listener: (change: RelayChange) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async getSnapshot(): Promise<RelaySnapshot> {
    return this.projector.getSnapshot();
  }

  async execute(command: RelayCommand): Promise<RelayCommandResult> {
    switch (command.type) {
      case "SetListening": {
        if (command.enabled && this.deps.audioStatus && !this.deps.audioStatus.ok) {
          return {
            ok: false,
            summary: "audio_unavailable",
            error: this.deps.audioStatus.detail || "audio_unavailable",
          };
        }
        await this.deps.store.setListening(this.deps.sessionId, command.enabled);
        this.emit({ type: "ListeningChanged", listening: command.enabled });
        await this.projector.emitSnapshot();
        return { ok: true, summary: command.enabled ? "listening_on" : "listening_off" };
      }
      case "SetHostedProcessing": {
        await this.deps.store.setHostedProcessingEnabled(command.enabled);
        const ambientResumed = command.enabled ? await this.ambient.resumeHostedWaitingCases() : 0;
        const judgmentResumed = command.enabled ? await this.resumeParkedJudgments() : 0;
        const resumed = ambientResumed + judgmentResumed;
        await this.projector.emitSnapshot();
        return {
          ok: true,
          summary: command.enabled
            ? resumed > 0
              ? `hosted_processing_on_resumed_${resumed}`
              : "hosted_processing_on"
            : "hosted_processing_off",
        };
      }
      case "GrantJevDisclosure": {
        if (command.scopeKind === "project" && !command.scopeId) {
          return { ok: false, summary: "scope_required", error: "scope_required" };
        }
        if (command.scopeKind === "session" && command.scopeId && command.scopeId !== this.deps.sessionId) {
          return { ok: false, summary: "wrong_scope", error: "wrong_scope" };
        }
        const now = this.deps.clock.now().toISOString();
        const built = buildHostedJudgmentGrant({
          grantId: this.deps.ids.next("grant"),
          scopeKind: command.scopeKind,
          scopeId: command.scopeKind === "session" ? this.deps.sessionId : command.scopeId ?? "",
          now,
          ttlMs: command.ttlMs,
          allowedSourceClasses: command.allowedSourceClasses,
          maxRequests: command.maxRequests,
          maxBytes: command.maxBytes,
        });
        if (!built.ok) return { ok: false, summary: built.reason, error: built.reason };
        await new HostedGrantLedger(grantAccountFor(this.deps.store)).save(built.grant, now);
        const hostedOn = await this.deps.store.getHostedProcessingEnabled();
        const ambientResumed = hostedOn ? await this.ambient.resumeHostedWaitingCases() : 0;
        const judgmentResumed = hostedOn ? await this.resumeParkedJudgments() : 0;
        const resumed = ambientResumed + judgmentResumed;
        await this.projector.emitSnapshot();
        return {
          ok: true,
          summary: resumed > 0 ? `jev_disclosure_granted_resumed_${resumed}` : "jev_disclosure_granted",
        };
      }
      case "RevokeJevDisclosure": {
        const ledger = new HostedGrantLedger(grantAccountFor(this.deps.store));
        const existing = await ledger.findById(command.grantId);
        if (!existing) return { ok: false, summary: "grant_missing", error: "grant_missing" };
        if (existing.revokedAt) return { ok: false, summary: "already_revoked", error: "already_revoked" };
        const now = this.deps.clock.now().toISOString();
        await ledger.revoke(command.grantId, now);
        await this.projector.emitSnapshot();
        return { ok: true, summary: "jev_disclosure_revoked" };
      }
      case "RefreshProviderHealth": {
        await this.projector.emitSnapshot();
        return { ok: true, summary: "provider_health_refreshed" };
      }
      case "CancelActive": {
        if (!this.running) return { ok: false, summary: "not_running", error: "not_running" };
        const caseId = this.activeCaseId;
        const previous = this.loopPromise;
        this.abort?.abort();
        await previous;
        if (!this.running) return { ok: true, summary: "cancelled", ...(caseId ? { caseId } : {}) };
        this.abort = new AbortController();
        this.loopPromise = this.dispatcher.runLoop(this.abort.signal);
        if (caseId) {
          const current = await this.deps.store.getCase(caseId);
          if (current && current.status !== "completed" && current.status !== "cancelled") {
            await this.deps.store.updateCase(caseId, current.version, {
              status: "cancelled",
              phase: current.phase,
              waitKind: null,
              at: this.deps.clock.now().toISOString(),
            });
          }
        }
        await this.trace.emit({
          type: "outcome.recorded",
          status: "failed",
          reasonCode: "cancelled",
          ...(caseId ? { caseId } : {}),
        });
        this.activeCaseId = null;
        await this.projector.emitSnapshot();
        return { ok: true, summary: "cancelled", ...(caseId ? { caseId } : {}) };
      }
      case "SubmitText": {
        await this.overlays.setInputPreview(command.text);
        const segment = this.intake.makeTypedSegment(command.text);
        const caseId = await this.intake.ingestFinalSegment(segment, true);
        if (caseId) await this.overlays.setInputPreview(command.text, caseId);
        return { ok: true, summary: "ask_accepted", caseId };
      }
      case "UpsertGlossaryEntry":
        return this.cases.upsertGlossary(command.token, command.expansion, command.confirmed, command.replace === true);
      case "CaptureBirthday":
        return this.cases.captureBirthday(command.displayName, command.date, command.confirmed, command.replace === true);
      case "DeleteMemory":
        await this.deps.store.learning.deleteMemory(command.kind, command.key);
        await this.projector.emitSnapshot();
        return { ok: true, summary: "deleted" };
      case "ApproveCandidate":
        return this.patterns.setCandidateState(command.candidateId, "approved", "user_approval");
      case "RejectCandidate":
        return this.patterns.setCandidateState(command.candidateId, "rejected", "user_reject");
      case "SnoozeCandidate":
        return this.patterns.setCandidateState(command.candidateId, "snoozed", "user_snooze");
      case "StartWorkSession":
        return this.patterns.startWorkSession();
      case "EndWorkSession":
        return this.patterns.endWorkSession();
      case "AcceptAmbientRecommendation":
        return this.ambient.acceptRecommendation(command.recommendationId);
      case "DismissAmbientRecommendation":
        return this.ambient.dismissRecommendation(command.recommendationId);
      case "FeedbackAmbientRecommendation":
        return this.ambient.feedbackRecommendation(command.recommendationId, command.feedback);
      default: {
        const handled = await this.operations.execute(command);
        if (handled) return handled;
        return { ok: false, summary: "unsupported_command", error: command.type };
      }
    }
  }

  private async resumeParkedJudgments(): Promise<number> {
    if (!(await this.deps.store.getHostedProcessingEnabled())) return 0;
    const now = this.deps.clock.now().toISOString();
    const grant = sessionDisclosureView(
      await new HostedGrantLedger(grantAccountFor(this.deps.store)).read({ kind: "session", id: this.deps.sessionId }),
      now,
    );
    if (!grant) return 0;
    const waiting = await listHostedWaits(this.deps.store);
    let resumed = 0;
    for (const item of waiting) {
      const current = await this.deps.store.getCase(item.caseId);
      if (!current || current.status !== "waiting" || current.waitKind !== "hosted_judgment") continue;
      const updated = await this.deps.store.updateCase(item.caseId, current.version, {
        status: "active",
        phase: "judge",
        waitKind: null,
        at: now,
      });
      if (!updated) continue;
      await markHostedWaitResumed(this.deps.store, item.caseId, now);
      await this.scheduler.enqueue(
        "judgment.requested",
        { ...item.payload, resumedOnce: true },
        PRIORITY_DIRECT,
        this.deps.ids,
        0,
        { correlationId: item.caseId },
      );
      resumed += 1;
    }
    return resumed;
  }

  /** Live/mic observation final — requires Listening ON unless this is a direct Ask. */
  async ingestFinalSegment(segment: TranscriptSegmentV1, isAsk: boolean): Promise<string> {
    return this.intake.ingestFinalSegment(segment, isAsk);
  }

  /**
   * Developer/fixture replay final — does not require Listening and must not start audio.
   * Only scripted_transcript / audio_file origins are accepted.
   */
  async ingestReplayFinalSegment(segment: TranscriptSegmentV1): Promise<string> {
    return this.intake.ingestReplayFinalSegment(segment);
  }

  async completeVerifiedWork(kind: string, fields: Readonly<Record<string, string>>): Promise<RelayCommandResult> {
    return this.patterns.completeVerifiedWork(kind, fields);
  }

  private emit(change: RelayChange): void {
    for (const listener of this.listeners) listener(change);
  }
}

export { PRIORITY_DIRECT, PRIORITY_OBSERVED };
