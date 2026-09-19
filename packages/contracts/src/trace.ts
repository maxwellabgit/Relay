export type TraceEventV1 = {
  readonly schemaVersion: 1;
  readonly sequence: number;
  readonly at: string;
  readonly type: string;
  readonly caseId?: string;
  readonly segmentId?: string;
  readonly judgmentId?: string;
  readonly reflexId?: string;
  readonly reflexVersion?: number;
  readonly probabilities?: Readonly<Record<string, number>>;
  readonly thresholds?: Readonly<Record<string, number>>;
  readonly selectedOutcome?: string;
  readonly reasonCode?: string;
  readonly latencyMs?: number;
  readonly artifactRef?: string;
  readonly queueDepth?: number;
  readonly waitState?: string;
};

export type RunManifestV1 = {
  readonly schemaVersion: 1;
  readonly runId: string;
  readonly gitCommit: string;
  readonly gitDirty: boolean;
  readonly appVersion: string;
  readonly protocolVersion: string;
  readonly os: string;
  readonly fixtureHashes: Readonly<Record<string, string>>;
  readonly modelNames: readonly string[];
  readonly reflexVersions: Readonly<Record<string, number>>;
  readonly policyHashes: Readonly<Record<string, string>>;
  readonly startedAt: string;
  readonly endedAt?: string;
};
