import type {
  ApprovalSnapshot,
  ConnectionStateSnapshot,
  ConnectorActionRef,
  ConnectorRef,
  OperationEnvelope,
  PendingOperationApproval,
  ReflexActivationState,
  ReflexRef,
  ReflexStateSnapshot,
} from "@relay/contracts";
import type { EngineStore } from "../store.js";

const SCAN = 8000;

const CONNECTION_UPSERTED = "authority.connection_upserted";
const CONNECTION_REMOVED = "authority.connection_removed";
const OPERATION_PROPOSED = "authority.operation_proposed";
const OPERATION_STATUS = "authority.operation_status";
const DISCLOSURE_GRANTED = "authority.disclosure_granted";
const DISCLOSURE_REVOKED = "authority.disclosure_revoked";
const REFLEX_STATE = "authority.reflex_state";
const AUTH_ATTEMPT = "authority.auth_attempt";

export type ConnectionRecord = {
  readonly connectionId: string;
  readonly connectionVersion: number;
  readonly connector: ConnectorRef;
  readonly connected: boolean;
  readonly observationEnabled: boolean;
  readonly healthStatus: string;
  readonly selectedResources: readonly string[];
  readonly writeActionEnabled: Readonly<Record<string, boolean>>;
  readonly grantedOAuthScopes: readonly string[];
  readonly readScopes: readonly string[];
};

export type DisclosureGrantRecord = {
  readonly grantId: string;
  readonly grantVersion: number;
  readonly connectionId: string;
  readonly disclosure: "hosted_session" | "hosted_project" | "public";
  readonly sensitivity: number;
  readonly purpose: string;
  readonly expiresAt?: string;
};

export type AuthAttemptRecord = {
  readonly authorizationAttemptId: string;
  readonly connectionId: string;
  readonly connector: ConnectorRef;
  readonly kind: "authorize" | "reauthorize";
  readonly status: "pending" | "completed" | "failed";
  readonly additionalOAuthScopes: readonly string[];
  readonly expectedConnectionVersion?: number;
};

export type AuthorityProjection = {
  readonly connections: readonly ConnectionRecord[];
  readonly approvals: readonly ApprovalSnapshot[];
  readonly operations: readonly OperationEnvelope[];
  readonly disclosures: readonly DisclosureGrantRecord[];
  readonly reflexes: readonly ReflexStateSnapshot[];
  readonly authAttempts: readonly AuthAttemptRecord[];
};

/**
 * Reconstructable authority state from domain events — no EngineStore schema change.
 */
export class AuthorityState {
  constructor(private readonly store: EngineStore) {}

  async project(): Promise<AuthorityProjection> {
    const events = await this.store.listDomainEvents(SCAN);
    const connections = new Map<string, ConnectionRecord>();
    const operations = new Map<string, OperationEnvelope>();
    const disclosures = new Map<string, DisclosureGrantRecord>();
    const reflexes = new Map<string, ReflexStateSnapshot>();
    const authAttempts = new Map<string, AuthAttemptRecord>();

    for (const event of events) {
      switch (event.type) {
        case CONNECTION_UPSERTED: {
          const row = event.payload.connection;
          if (isConnection(row)) connections.set(row.connectionId, row);
          break;
        }
        case CONNECTION_REMOVED: {
          const id = event.payload.connectionId;
          if (typeof id === "string") connections.delete(id);
          break;
        }
        case OPERATION_PROPOSED: {
          const row = event.payload.operation;
          if (isOperation(row)) operations.set(row.operationId, row);
          break;
        }
        case OPERATION_STATUS: {
          const id = event.payload.operationId;
          const status = event.payload.status;
          if (typeof id !== "string" || typeof status !== "string") break;
          const current = operations.get(id);
          if (!current) break;
          operations.set(id, { ...current, status: status as OperationEnvelope["status"] });
          break;
        }
        case DISCLOSURE_GRANTED: {
          const row = event.payload.grant;
          if (isDisclosure(row)) disclosures.set(row.grantId, row);
          break;
        }
        case DISCLOSURE_REVOKED: {
          const id = event.payload.grantId;
          if (typeof id === "string") disclosures.delete(id);
          break;
        }
        case REFLEX_STATE: {
          const row = event.payload.reflex;
          if (isReflex(row)) {
            reflexes.set(`${row.reflex.id}@${row.reflex.version}`, row);
          }
          break;
        }
        case AUTH_ATTEMPT: {
          const row = event.payload.attempt;
          if (isAuthAttempt(row)) authAttempts.set(row.authorizationAttemptId, row);
          break;
        }
        default:
          break;
      }
    }

    const approvals: ApprovalSnapshot[] = [...operations.values()]
      .filter((op) => op.status === "awaiting_approval" || op.status === "proposed")
      .map((op) => ({
        operationId: op.operationId,
        action: op.action,
        summary: op.summary,
        canonicalHash: op.canonicalHash,
        caseVersion: op.caseVersion,
        connectionId: op.connectionId,
        connectionVersion: op.connectionVersion,
        proposedAt: op.proposedAt,
      }));

    return {
      connections: [...connections.values()],
      approvals,
      operations: [...operations.values()],
      disclosures: [...disclosures.values()],
      reflexes: [...reflexes.values()],
      authAttempts: [...authAttempts.values()].filter((a) => a.status === "pending"),
    };
  }

  async upsertConnection(connection: ConnectionRecord, at: string): Promise<void> {
    await this.store.appendDomainEvent(CONNECTION_UPSERTED, at, { connection });
  }

  async removeConnection(connectionId: string, at: string): Promise<void> {
    await this.store.appendDomainEvent(CONNECTION_REMOVED, at, { connectionId });
  }

  async proposeOperation(operation: OperationEnvelope, at: string): Promise<void> {
    await this.store.appendDomainEvent(OPERATION_PROPOSED, at, { operation });
  }

  async setOperationStatus(
    operationId: string,
    status: OperationEnvelope["status"],
    at: string,
  ): Promise<void> {
    await this.store.appendDomainEvent(OPERATION_STATUS, at, { operationId, status });
  }

  async grantDisclosure(grant: DisclosureGrantRecord, at: string): Promise<void> {
    await this.store.appendDomainEvent(DISCLOSURE_GRANTED, at, { grant });
  }

  async revokeDisclosure(grantId: string, at: string): Promise<void> {
    await this.store.appendDomainEvent(DISCLOSURE_REVOKED, at, { grantId });
  }

  async setReflexState(reflex: ReflexStateSnapshot, at: string): Promise<void> {
    await this.store.appendDomainEvent(REFLEX_STATE, at, { reflex });
  }

  async putAuthAttempt(attempt: AuthAttemptRecord, at: string): Promise<void> {
    await this.store.appendDomainEvent(AUTH_ATTEMPT, at, { attempt });
  }

  toPendingApproval(
    operation: OperationEnvelope,
    connection: ConnectionRecord | undefined,
  ): PendingOperationApproval | null {
    if (!connection) return null;
    const actionKey = `${operation.action.actionId}@${operation.action.actionVersion}`;
    return {
      operationId: operation.operationId,
      action: operation.action,
      canonicalHash: operation.canonicalHash,
      caseVersion: operation.caseVersion,
      connectionId: operation.connectionId,
      connectionVersion: operation.connectionVersion,
      grantedScopeKeys: new Set(connection.grantedOAuthScopes),
      writeActionEnabled: connection.writeActionEnabled[actionKey] === true,
      boundActionVersion: operation.action.actionVersion,
    };
  }

  toConnectionSnapshots(rows: readonly ConnectionRecord[]): ConnectionStateSnapshot[] {
    return rows.map((row) => ({
      connectionId: row.connectionId,
      connectionVersion: row.connectionVersion,
      connector: row.connector,
      connected: row.connected,
      observationEnabled: row.observationEnabled,
      healthStatus: row.healthStatus,
      selectedResources: row.selectedResources,
      writeActionEnabled: row.writeActionEnabled,
      grantedOAuthScopes: row.grantedOAuthScopes,
    }));
  }
}

function isConnection(value: unknown): value is ConnectionRecord {
  if (!value || typeof value !== "object") return false;
  const row = value as Partial<ConnectionRecord>;
  return typeof row.connectionId === "string" && typeof row.connectionVersion === "number";
}

function isOperation(value: unknown): value is OperationEnvelope {
  if (!value || typeof value !== "object") return false;
  const row = value as Partial<OperationEnvelope>;
  return typeof row.operationId === "string" && typeof row.canonicalHash === "string";
}

function isDisclosure(value: unknown): value is DisclosureGrantRecord {
  if (!value || typeof value !== "object") return false;
  const row = value as Partial<DisclosureGrantRecord>;
  return typeof row.grantId === "string" && typeof row.grantVersion === "number";
}

function isReflex(value: unknown): value is ReflexStateSnapshot {
  if (!value || typeof value !== "object") return false;
  const row = value as Partial<ReflexStateSnapshot>;
  const reflex = row.reflex as ReflexRef | undefined;
  return (
    !!reflex &&
    typeof reflex.id === "string" &&
    typeof reflex.version === "number" &&
    typeof row.stateVersion === "number" &&
    typeof row.activation === "string"
  );
}

function isAuthAttempt(value: unknown): value is AuthAttemptRecord {
  if (!value || typeof value !== "object") return false;
  const row = value as Partial<AuthAttemptRecord>;
  return typeof row.authorizationAttemptId === "string" && typeof row.connectionId === "string";
}

export function formatActionKey(action: ConnectorActionRef): string {
  return `${action.actionId}@${action.actionVersion}`;
}

export type { ReflexActivationState };
