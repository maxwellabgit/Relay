export type VerifyEvidenceStatus = "Verified" | "Contradicted" | "Needs evidence" | "Proposal";

export type VerifyDisposition = "pending" | "accepted" | "dismissed" | "superseded";

export type VerifySourceRef = {
  readonly provider: string;
  readonly externalEventId: string;
  readonly revision: string;
  readonly artifactId: string | null;
  readonly at: string;
};

export type VerifyItem = {
  readonly verifyId: string;
  readonly version: number;
  readonly evidenceStatus: VerifyEvidenceStatus;
  readonly disposition: VerifyDisposition;
  readonly reason: string;
  readonly proposedChange: string;
  readonly projectCaseIds: readonly string[];
  readonly sources: readonly VerifySourceRef[];
  readonly dedupeKey: string;
  readonly unreadCount: number;
  readonly caseVersion: number;
  readonly replaceEntryId: string | null;
  readonly acceptedArtifactId: string | null;
  readonly acceptedSha256: string | null;
  readonly proposedArtifactId: string | null;
  readonly proposedSha256: string | null;
  readonly connectionId: string | null;
  readonly resourceId: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly undoOf: string | null;
};

export type VerifyItemView = {
  readonly verifyId: string;
  readonly version: number;
  readonly evidenceStatus: VerifyEvidenceStatus;
  readonly disposition: VerifyDisposition;
  readonly reason: string;
  readonly proposedChange: string;
  readonly projectCaseIds: readonly string[];
  readonly unreadCount: number;
  readonly updatedAt: string;
  readonly acceptedText: string;
  readonly proposedText: string;
};
