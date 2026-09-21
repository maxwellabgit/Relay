export type JevHealthState = {
  ok: boolean;
  detail: string;
};

export type JevEvidenceInput = {
  readonly secretPresent: boolean;
  readonly hostedEnabled: boolean;
  /** Last observed provider outcome for this process. */
  readonly lastOutcome: "none" | "success" | "failure";
};

/**
 * Evidence-based Jev chip. Periodic refresh must not invent "ready" without a successful request.
 */
export function projectJevHealth(input: JevEvidenceInput): JevHealthState {
  if (!input.secretPresent) {
    return { ok: false, detail: "missing key" };
  }
  if (!input.hostedEnabled) {
    return { ok: false, detail: "hosted off" };
  }
  if (input.lastOutcome === "success") {
    return { ok: true, detail: "ready" };
  }
  if (input.lastOutcome === "failure") {
    return { ok: false, detail: "degraded" };
  }
  return { ok: true, detail: "configured" };
}

export class JevHealthTracker {
  private secretPresent = false;
  private hostedEnabled = false;
  private lastOutcome: "none" | "success" | "failure" = "none";
  readonly state: JevHealthState = { ok: false, detail: "missing key" };

  apply(input: { readonly secretPresent: boolean; readonly hostedEnabled: boolean }): JevHealthState {
    this.secretPresent = input.secretPresent;
    this.hostedEnabled = input.hostedEnabled;
    return this.refresh();
  }

  noteSuccess(): JevHealthState {
    this.lastOutcome = "success";
    return this.refresh();
  }

  noteFailure(): JevHealthState {
    this.lastOutcome = "failure";
    return this.refresh();
  }

  private refresh(): JevHealthState {
    const next = projectJevHealth({
      secretPresent: this.secretPresent,
      hostedEnabled: this.hostedEnabled,
      lastOutcome: this.lastOutcome,
    });
    this.state.ok = next.ok;
    this.state.detail = next.detail;
    return this.state;
  }
}
