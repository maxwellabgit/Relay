import {
  appendFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  writeFileSync,
} from "node:fs";
import { join } from "node:path";
import { execSync } from "node:child_process";
import type { RunManifestV1, TraceEventV1 } from "@relay/contracts";

export type DiagnosticsOptions = {
  readonly runsRoot: string;
  readonly appVersion: string;
  readonly protocolVersion: string;
  readonly flushEveryLine?: boolean;
};

export class RunDiagnostics {
  readonly runId: string;
  readonly runDir: string;
  private sequence = 0;

  constructor(private readonly options: DiagnosticsOptions) {
    this.runId = `run_${Date.now().toString(36)}`;
    this.runDir = join(options.runsRoot, this.runId);
    mkdirSync(join(this.runDir, "artifacts"), { recursive: true });
    mkdirSync(join(this.runDir, "screenshots"), { recursive: true });
    mkdirSync(join(this.runDir, "halo"), { recursive: true });
    writeFileSync(join(this.runDir, "events.jsonl"), "", "utf8");
  }

  writeManifest(partial: Partial<RunManifestV1> = {}): RunManifestV1 {
    const git = gitInfo();
    const manifest: RunManifestV1 = {
      schemaVersion: 1,
      runId: this.runId,
      gitCommit: git.commit,
      gitDirty: git.dirty,
      appVersion: this.options.appVersion,
      protocolVersion: this.options.protocolVersion,
      os: process.platform,
      fixtureHashes: {},
      modelNames: ["recorded"],
      reflexVersions: { "reflex.resolve-acronym": 1 },
      policyHashes: {},
      startedAt: new Date().toISOString(),
      ...partial,
    };
    writeFileSync(join(this.runDir, "manifest.json"), JSON.stringify(manifest, null, 2), "utf8");
    return manifest;
  }

  append(event: Omit<TraceEventV1, "schemaVersion" | "sequence" | "at"> & { at?: string }): void {
    this.sequence += 1;
    const line: TraceEventV1 = {
      schemaVersion: 1,
      sequence: this.sequence,
      at: event.at ?? new Date().toISOString(),
      type: event.type,
      ...(event.caseId ? { caseId: event.caseId } : {}),
      ...(event.segmentId ? { segmentId: event.segmentId } : {}),
      ...(event.judgmentId ? { judgmentId: event.judgmentId } : {}),
      ...(event.reflexId ? { reflexId: event.reflexId } : {}),
      ...(event.reflexVersion != null ? { reflexVersion: event.reflexVersion } : {}),
      ...(event.probabilities ? { probabilities: event.probabilities } : {}),
      ...(event.thresholds ? { thresholds: event.thresholds } : {}),
      ...(event.selectedOutcome ? { selectedOutcome: event.selectedOutcome } : {}),
      ...(event.reasonCode ? { reasonCode: event.reasonCode } : {}),
      ...(event.latencyMs != null ? { latencyMs: event.latencyMs } : {}),
      ...(event.artifactRef ? { artifactRef: event.artifactRef } : {}),
      ...(event.queueDepth != null ? { queueDepth: event.queueDepth } : {}),
      ...(event.waitState ? { waitState: event.waitState } : {}),
    };
    appendFileSync(join(this.runDir, "events.jsonl"), `${JSON.stringify(line)}\n`, "utf8");
  }

  writeSnapshot(snapshot: unknown): void {
    writeFileSync(join(this.runDir, "final-snapshot.json"), JSON.stringify(snapshot, null, 2), "utf8");
  }

  end(): void {
    const path = join(this.runDir, "manifest.json");
    if (!existsSync(path)) return;
    const manifest = JSON.parse(readFileSync(path, "utf8")) as RunManifestV1;
    const ended: RunManifestV1 = { ...manifest, endedAt: new Date().toISOString() };
    writeFileSync(path, JSON.stringify(ended, null, 2), "utf8");
  }
}

function gitInfo(): { commit: string; dirty: boolean } {
  try {
    const commit = execSync("git rev-parse HEAD", { encoding: "utf8" }).trim();
    const dirty = execSync("git status --porcelain", { encoding: "utf8" }).trim().length > 0;
    return { commit, dirty };
  } catch {
    return { commit: "unknown", dirty: true };
  }
}

export function latestRunDir(runsRoot: string): string | null {
  if (!existsSync(runsRoot)) return null;
  const dirs = readdirSync(runsRoot)
    .filter((name) => name.startsWith("run_"))
    .sort();
  const last = dirs.at(-1);
  return last ? join(runsRoot, last) : null;
}

export type ThresholdEvalInput = {
  readonly probabilities: Readonly<Record<string, number>>;
  readonly usefulYes: number;
  readonly selected: string;
  readonly policy: {
    readonly choiceProbabilityMinimum: number;
    readonly choiceMarginMinimum: number;
    readonly displayUsefulnessMinimum: number;
  };
  readonly isExplicitAsk?: boolean;
};

export function evalThresholdDecision(input: ThresholdEvalInput): {
  show: boolean;
  reasonCode: string;
} {
  if (input.selected === "no_match") return { show: false, reasonCode: "no_match" };
  const probs = Object.entries(input.probabilities).sort((a, b) => b[1]! - a[1]!);
  const top = probs[0];
  const second = probs[1];
  if (!top || top[1]! < input.policy.choiceProbabilityMinimum) {
    return { show: false, reasonCode: "below_choice_minimum" };
  }
  if (second && top[1]! - second[1]! < input.policy.choiceMarginMinimum) {
    return { show: false, reasonCode: "below_choice_margin" };
  }
  if (!input.isExplicitAsk && input.usefulYes < input.policy.displayUsefulnessMinimum) {
    return { show: false, reasonCode: "below_usefulness" };
  }
  return { show: true, reasonCode: "policy_pass" };
}
