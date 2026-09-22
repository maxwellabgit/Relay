import {
  combinePolicies,
  localOnlyPolicy,
  type ArtifactProvenance,
  type ArtifactRef,
  type DataPolicy,
} from "./artifacts.js";

/**
 * In-process seal for artifact policy. The first seal wins, and any later
 * seal may only tighten it. A caller cannot relabel local-only bytes as
 * remotely eligible, including by copying them into a new id.
 */
export class ProvenanceIndex {
  private readonly byId = new Map<string, ArtifactProvenance>();

  seal(
    artifactId: string,
    sha256: string,
    requested: DataPolicy,
    derivedFrom: readonly ArtifactRef[] = [],
  ): ArtifactProvenance {
    let policy = requested;
    const derived: ArtifactProvenance["derivedFrom"][number][] = [];
    for (const ref of derivedFrom) {
      const known = this.byId.get(ref.artifactId);
      const contributor =
        known && known.sha256 === ref.sha256 ? known.policy : localOnlyPolicy();
      policy = combinePolicies(policy, contributor);
      derived.push({
        artifactId: ref.artifactId,
        sha256: ref.sha256,
        disclosure: contributor.disclosure,
      });
    }
    for (const row of this.byId.values()) {
      if (row.sha256 === sha256) policy = combinePolicies(policy, row.policy);
    }
    const prior = this.byId.get(artifactId);
    if (prior) {
      const next: ArtifactProvenance = {
        ...prior,
        policy: combinePolicies(prior.policy, policy),
      };
      this.byId.set(artifactId, next);
      return next;
    }
    const created: ArtifactProvenance = {
      artifactId,
      sha256,
      policy,
      derivedFrom: derived,
    };
    this.byId.set(artifactId, created);
    return created;
  }

  get(artifactId: string): ArtifactProvenance | null {
    return this.byId.get(artifactId) ?? null;
  }

  load(row: ArtifactProvenance): void {
    const prior = this.byId.get(row.artifactId);
    if (!prior) {
      this.byId.set(row.artifactId, row);
      return;
    }
    this.byId.set(row.artifactId, {
      ...prior,
      policy: combinePolicies(prior.policy, row.policy),
    });
  }
}
