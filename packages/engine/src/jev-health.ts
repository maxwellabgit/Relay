export type JevHealthName =
  | "unconfigured"
  | "configured_not_tested"
  | "healthy_live"
  | "degraded"
  | "unavailable"
  | "disabled";

export type JevHealthState = {
  ok: boolean;
  state: JevHealthName;
  detail: string;
};

export type JevEvidenceInput = {
  readonly secretPresent: boolean;
  readonly hostedEnabled: boolean;
  /** Only a successful live canary may produce healthy_live. */
  readonly liveCanary: "none" | "success" | "failure";
  readonly availability?: "unknown" | "unreachable";
};

const DETAIL: Record<JevHealthName, string> = {
  unconfigured: "missing key",
  configured_not_tested: "configured",
  healthy_live: "ready",
  degraded: "degraded",
  unavailable: "unavailable",
  disabled: "hosted off",
};

function named(state: JevHealthName): JevHealthState {
  return { ok: state === "healthy_live", state, detail: DETAIL[state] };
}

/**
 * Evidence-based Jev chip. Configuration alone is not healthy_live.
 */
export function projectJevHealth(input: JevEvidenceInput): JevHealthState {
  if (!input.secretPresent) return named("unconfigured");
  if (!input.hostedEnabled) return named("disabled");
  if (input.availability === "unreachable") return named("unavailable");
  if (input.liveCanary === "failure") return named("degraded");
  if (input.liveCanary === "success") return named("healthy_live");
  return named("configured_not_tested");
}

export class JevHealthTracker {
  private secretPresent = false;
  private hostedEnabled = false;
  private liveCanary: "none" | "success" | "failure" = "none";
  private availability: "unknown" | "unreachable" = "unknown";
  readonly state: JevHealthState = named("unconfigured");

  apply(input: { readonly secretPresent: boolean; readonly hostedEnabled: boolean }): JevHealthState {
    this.secretPresent = input.secretPresent;
    this.hostedEnabled = input.hostedEnabled;
    return this.refresh();
  }

  /** Ordinary judgment success is not a live canary. */
  noteSuccess(): JevHealthState {
    if (this.availability === "unreachable") this.availability = "unknown";
    return this.refresh();
  }

  noteLiveCanary(): JevHealthState {
    this.liveCanary = "success";
    this.availability = "unknown";
    return this.refresh();
  }

  noteFailure(): JevHealthState {
    this.liveCanary = "failure";
    return this.refresh();
  }

  noteUnavailable(): JevHealthState {
    this.availability = "unreachable";
    return this.refresh();
  }

  private refresh(): JevHealthState {
    const next = projectJevHealth({
      secretPresent: this.secretPresent,
      hostedEnabled: this.hostedEnabled,
      liveCanary: this.liveCanary,
      availability: this.availability,
    });
    this.state.ok = next.ok;
    this.state.state = next.state;
    this.state.detail = next.detail;
    return this.state;
  }
}
