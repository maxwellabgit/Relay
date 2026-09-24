export type EventOrigin = "speech" | "typed" | "connected" | "scheduler" | "task";

export type EventEnvelope = {
  readonly schemaVersion: 1;
  readonly eventId: string;
  readonly origin: EventOrigin;
  readonly provider: string;
  readonly sourceId: string;
  readonly resourceId: string;
  readonly externalEventId: string;
  readonly revision: string;
  readonly observedAt: string;
  readonly occurredAt: string;
  readonly dedupeKey: string;
  readonly contentRef: string | null;
  /** Protected body. Callers must not copy this into logs. */
  readonly content: string | null;
  readonly selected: boolean;
  readonly status: "active" | "withdrawn";
};

export type ObservationBinding = {
  readonly bindingId: string;
  readonly connectionId: string;
  readonly resourceId: string;
  readonly projectCaseIds: readonly string[];
  readonly eventKinds: readonly string[];
  readonly contentLevel: "metadata" | "excerpt";
  readonly retention: "case_entry" | "artifact_only" | "none";
  readonly enabled: boolean;
  readonly revoked: boolean;
  readonly lastSyncAt: string | null;
  readonly lagMs: number | null;
};

export type SourceCursor = {
  readonly cursorId: string;
  readonly bindingId: string;
  readonly provider: string;
  readonly resourceId: string;
  readonly token: string;
  readonly updatedAt: string;
};

export type EventReceipt = {
  readonly receiptId: string;
  readonly dedupeKey: string;
  readonly provider: string;
  readonly externalEventId: string;
  readonly revision: string;
  readonly status: "processed" | "ignored" | "withdrawn";
  readonly projectCaseIds: readonly string[];
  readonly executionId: string | null;
  readonly reflexInvocationId: string | null;
  readonly createdAt: string;
};

export type ScopedActionGrant = {
  readonly grantId: string;
  readonly reflexId: string;
  readonly reflexVersion: number;
  readonly connectionId: string;
  readonly resourceIds: readonly string[];
  readonly actionId: string;
  readonly expiresAt: string;
  readonly maxPerHour: number;
};

/** Production-shaped port. The deterministic fixture implements this in test code only. */
export type ExternalEventAdapter = {
  readonly provider: string;
  pull(signal: AbortSignal): Promise<readonly EventEnvelope[]>;
};
