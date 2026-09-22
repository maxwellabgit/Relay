/** Versioned connector / action / judgment / reflex identity. */

export type ConnectorRef = {
  readonly id: string;
  readonly version: number;
};

export type ConnectorActionRef = {
  readonly connectorId: string;
  readonly connectorVersion: number;
  readonly actionId: string;
  readonly actionVersion: number;
};

export type JudgmentDefinitionRef = {
  readonly id: string;
  readonly version: number;
};

export type ReflexRef = {
  readonly id: string;
  readonly version: number;
};

export type ArtifactRef = {
  readonly artifactId: string;
  readonly sha256: string;
  readonly policy: DataPolicy;
};

export type DisclosureClass = "local_only" | "hosted_session" | "hosted_project" | "public";

export type DataSensitivity = number;

export const DataSensitivityFlags = {
  None: 0,
  Personal: 1,
  Email: 2,
  Calendar: 4,
  SourceCode: 8,
  Financial: 16,
} as const;

export type DataPolicy = {
  readonly disclosure: DisclosureClass;
  readonly sensitivity: DataSensitivity;
};

const disclosureRank: Record<DisclosureClass, number> = {
  local_only: 0,
  hosted_session: 1,
  hosted_project: 2,
  public: 3,
};

export function localOnlyPolicy(sensitivity: DataSensitivity = DataSensitivityFlags.None): DataPolicy {
  return { disclosure: "local_only", sensitivity };
}

export function hostedSessionPolicy(sensitivity: DataSensitivity = DataSensitivityFlags.None): DataPolicy {
  return { disclosure: "hosted_session", sensitivity };
}

/** Immutable record written when an artifact is sealed. Callers cannot loosen it later. */
export type ArtifactProvenance = {
  readonly artifactId: string;
  readonly sha256: string;
  readonly policy: DataPolicy;
  readonly derivedFrom: readonly {
    readonly artifactId: string;
    readonly sha256: string;
    readonly disclosure: DisclosureClass;
  }[];
};

export function rankDisclosure(disclosure: DisclosureClass): number {
  return disclosureRank[disclosure];
}

export function publicPolicy(): DataPolicy {
  return { disclosure: "public", sensitivity: DataSensitivityFlags.None };
}

export function observedPolicy(sensitivity: DataSensitivity): DataPolicy {
  return { disclosure: "local_only", sensitivity };
}

export function combinePolicies(a: DataPolicy, b: DataPolicy): DataPolicy {
  const disclosure =
    disclosureRank[a.disclosure] <= disclosureRank[b.disclosure] ? a.disclosure : b.disclosure;
  return { disclosure, sensitivity: a.sensitivity | b.sensitivity };
}

export function isHostedEligible(policy: DataPolicy): boolean {
  return (
    policy.disclosure === "hosted_session" ||
    policy.disclosure === "hosted_project" ||
    policy.disclosure === "public"
  );
}

export function formatConnectorRef(ref: ConnectorRef): string {
  return `${ref.id}@${ref.version}`;
}

export function formatConnectorActionRef(ref: ConnectorActionRef): string {
  return `${ref.connectorId}@${ref.connectorVersion}/${ref.actionId}@${ref.actionVersion}`;
}

export function formatReflexRef(ref: ReflexRef): string {
  return `${ref.id}@${ref.version}`;
}
