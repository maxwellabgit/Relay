/**
 * Live diagnostics path and summary contracts.
 * Desktop, Node harness, replay CLI, and tests share the same roots via DiagnosticPathPort.
 */

export type DiagnosticLatestPointer = {
  readonly schemaVersion: 1;
  readonly runId: string;
  readonly runDir: string;
  readonly commit: string;
  readonly profile: string;
  readonly mode: "live" | "replay" | "test" | "e2e";
  readonly startedAt: string;
  readonly heartbeatAt: string;
  readonly pid: number | null;
  readonly cleanShutdown: boolean | null;
  readonly uncleanShutdown: boolean;
};

export type DiagnosticLiveSummary = {
  readonly schemaVersion: 1;
  readonly runId: string;
  readonly commit: string;
  readonly profile: string;
  readonly providerReadiness: {
    readonly jev: string;
    readonly model: string;
    readonly audio: string;
  };
  readonly queue: {
    readonly ready: number;
    readonly oldestReadyMs: number;
    readonly deadLetters: number;
  };
  readonly activeCases: number;
  readonly waitingCases: number;
  readonly failedCases: number;
  readonly latestCase: {
    readonly caseId: string | null;
    readonly stage: string | null;
    readonly blocker: string | null;
  };
  readonly latestJudgment: {
    readonly questionSet: string | null;
    readonly selectedRoute: string | null;
    readonly policyResult: string | null;
    readonly observed: boolean;
  };
  readonly latestTool: {
    readonly toolId: string | null;
    readonly result: string | null;
    readonly observed: boolean;
  };
  readonly deadLetters: number;
  readonly retrySchedule: string | null;
  readonly paths: {
    readonly runDir: string;
    readonly eventsPath: string;
    readonly liveSummaryPath: string;
    readonly latestPointerPath: string;
  };
  readonly updatedAt: string;
};

export type DiagnosticPathPort = {
  /** Root that holds latest.json and runs/ */
  diagnosticsRoot(): string;
  runsRoot(): string;
  runDir(runId: string): string;
  latestPointerPath(): string;
  liveSummaryPath(runId: string): string;
  heartbeatPath(runId: string): string;
  eventsPath(runId: string): string;
};

/** Env override used by tests and headed E2E isolated profiles. */
export const DIAGNOSTICS_ROOT_ENV = "RELAY_DIAGNOSTICS_ROOT";
