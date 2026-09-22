import type { RelaySnapshot } from "@relay/contracts";

export type RedactedDiagnostics = {
  readonly manifest: {
    readonly runId: string;
    readonly commit: string;
    readonly mode: string;
    readonly deadLetters: number;
  };
  readonly status: RelaySnapshot["status"];
  readonly queueDepth: number;
  readonly cases: RelaySnapshot["cases"];
  readonly waits: RelaySnapshot["waits"];
  readonly decision: RelaySnapshot["decision"];
  readonly gate: RelaySnapshot["gate"];
  readonly trace: RelaySnapshot["trace"];
  readonly feed: readonly { readonly itemId: string; readonly kind: string; readonly summary: string }[];
};

/** Consumer export. Omits source transcripts, memories, and any field this builder does not copy. */
export function buildRedactedDiagnostics(snapshot: RelaySnapshot): RedactedDiagnostics {
  return {
    manifest: {
      runId: snapshot.runtime.runId,
      commit: snapshot.runtime.commit,
      mode: snapshot.runtime.mode,
      deadLetters: snapshot.runtime.deadLetters,
    },
    status: snapshot.status,
    queueDepth: snapshot.queueDepth,
    cases: snapshot.cases,
    waits: snapshot.waits,
    decision: snapshot.decision,
    gate: snapshot.gate,
    trace: snapshot.trace,
    feed: snapshot.feedItems.map((item) => ({
      itemId: item.itemId,
      kind: item.kind,
      summary: item.summary,
    })),
  };
}
