import type { RelayChange, RelaySnapshot, StatusChipState } from "@relay/contracts";
import { inspectRuntime } from "../inspect.js";
import { RETENTION_LABEL } from "../learning-store.js";
import { projectSnapshot } from "../projections.js";
import type { EngineStore } from "../store.js";
import type { ArtifactStorePort } from "@relay/contracts";
import type { TraceSink } from "../trace-sink.js";
import type { EngineTrace } from "../engine-helpers.js";
import type { OverlayState } from "./OverlayState.js";
import type { AuthorityState } from "../operations/AuthorityState.js";

export type SnapshotProjectorDeps = {
  readonly store: EngineStore;
  readonly artifacts: ArtifactStorePort;
  readonly sessionId: string;
  readonly overlays: OverlayState;
  readonly authority: AuthorityState;
  readonly trace: EngineTrace;
  readonly traceSink?: TraceSink;
  readonly storageDetail?: string;
  readonly jevStatus?: { ok: boolean; detail: string };
  readonly modelStatus?: { ok: boolean; detail: string; model?: string | null };
  readonly audioStatus?: { ok: boolean; detail: string };
  readonly mode?: "live" | "recorded" | "replay";
  readonly gitCommit?: string;
  readonly getRunning: () => boolean;
  readonly getActiveCaseId: () => string | null;
  readonly getActiveEpisodeId: () => string | null;
  readonly runId: () => string;
  readonly emit: (change: RelayChange) => void;
  readonly now?: () => string;
};

export class SnapshotProjector {
  constructor(private readonly deps: SnapshotProjectorDeps) {}

  statusChips(): StatusChipState[] {
    const jev = this.deps.jevStatus ?? { ok: false, detail: "missing key" };
    const model = this.deps.modelStatus ?? { ok: false, detail: "disabled" };
    const audio = this.deps.audioStatus ?? { ok: false, detail: "not connected" };
    const modelDetail =
      model.ok && model.model
        ? `${model.detail} · ${model.model}`
        : model.detail;
    return [
      { id: "engine", label: "Engine", ok: this.deps.getRunning(), detail: this.deps.getRunning() ? "running" : "stopped" },
      { id: "jev", label: "Jev", ok: jev.ok, detail: jev.detail },
      { id: "model", label: "Model", ok: model.ok, detail: modelDetail },
      { id: "audio", label: "Audio", ok: audio.ok, detail: audio.detail },
      { id: "halo", label: "Halo", ok: false, detail: "offline" },
      {
        id: "storage",
        label: "Storage",
        ok: true,
        detail: this.deps.storageDetail ?? "memory",
      },
    ];
  }

  async getSnapshot(): Promise<RelaySnapshot> {
    const snapshot = await projectSnapshot(
      this.deps.store,
      this.deps.sessionId,
      this.statusChips(),
      this.deps.artifacts,
      this.deps.now?.() ?? new Date().toISOString(),
    );
    const deadLetters = (await this.deps.store.listDeadLetters()).length;
    const caseStatusById = new Map(
      snapshot.cases.map((item) => [item.caseId, item.status as "active" | "waiting" | "completed" | "blocked" | "failed"]),
    );
    const logError = this.deps.trace.getLogError();
    const inspected = await inspectRuntime(
      this.deps.store.learning,
      this.deps.trace.list(),
      {
        runId: this.deps.runId(),
        commit: this.deps.gitCommit ?? "unknown",
        queueDepth: snapshot.queueDepth,
        logPath: this.deps.traceSink?.directoryLabel ?? "",
        logWritable: logError === null && this.deps.traceSink != null,
        logError,
        mode: this.deps.mode ?? "live",
        retention: RETENTION_LABEL,
        deadLetters,
        storageAdapter: this.deps.storageDetail ?? "memory",
        activeCaseId: this.deps.getActiveCaseId(),
        episodeId: this.deps.getActiveEpisodeId(),
      },
      caseStatusById,
    );
    const overlayLabels =
      inspected.gate != null ? await this.deps.overlays.getOptionLabels(inspected.gate.gateId) : {};
    const gate = inspected.gate
      ? { ...inspected.gate, optionLabels: { ...inspected.gate.optionLabels, ...overlayLabels } }
      : null;
    const [currentInputPreview, actions] = await Promise.all([
      this.deps.overlays.getInputPreview(),
      this.deps.overlays.listActions(),
    ]);
    const authority = await this.deps.authority.project();
    return {
      ...snapshot,
      ...inspected,
      gate,
      decision: inspected.decision,
      caseExecution: inspected.caseExecution,
      currentInputPreview,
      actions,
      approvals: authority.approvals,
      connections: this.deps.authority.toConnectionSnapshots(authority.connections),
      reflexes: authority.reflexes,
    };
  }

  async emitSnapshot(): Promise<void> {
    const snapshot = await this.getSnapshot();
    this.deps.emit({ type: "SnapshotReplaced", snapshot });
  }
}
