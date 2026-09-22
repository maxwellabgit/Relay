import type {
  ConnectorActionRef,
  RelayCommand,
  RelayCommandResult,
} from "@relay/contracts";
import {
  scopeStillGranted,
  tryApprove,
  tryMutateConnection,
  tryMutateReflex,
  tryReject,
} from "@relay/contracts";
import type { Clock, IdFactory } from "../scheduler.js";
import type { EngineTrace } from "../engine-helpers.js";
import type { PatternService } from "../learning/PatternService.js";
import {
  formatActionKey,
  type AuthorityState,
  type AuthAttemptRecord,
  type ConnectionRecord,
  type DisclosureGrantRecord,
} from "./AuthorityState.js";

export type OperationServiceDeps = {
  readonly authority: AuthorityState;
  readonly clock: Clock;
  readonly ids: IdFactory;
  readonly trace: EngineTrace;
  readonly emitSnapshot: () => Promise<void>;
  readonly patterns?: PatternService;
};

/**
 * Makes declared approval / connection / disclosure / reflex commands executable.
 * Effects stay inside typed authority records — no provider side effects here.
 */
export class OperationService {
  constructor(private readonly deps: OperationServiceDeps) {}

  async execute(command: RelayCommand): Promise<RelayCommandResult | null> {
    switch (command.type) {
      case "ApproveOperation":
        return this.approve(command.operationId, command.expectedCanonicalHash, command.expectedCaseVersion);
      case "RejectOperation":
        return this.reject(
          command.operationId,
          command.expectedCanonicalHash,
          command.expectedCaseVersion,
          command.reason,
        );
      case "StartConnectionAuthorization":
        return this.startAuthorization(command.connector.id, command.connector.version);
      case "CompleteConnectionAuthorization":
        return this.completeAuthorization(command.authorizationAttemptId, command.providerCallbackRef);
      case "DiscoverConnectionResources":
        return this.discoverResources(command.connectionId, command.expectedConnectionVersion);
      case "SelectConnectionResources":
        return this.selectResources(
          command.connectionId,
          command.expectedConnectionVersion,
          command.selectedResourceIds,
        );
      case "StartConnectionReauthorization":
        return this.startReauthorization(
          command.connectionId,
          command.expectedConnectionVersion,
          command.additionalOAuthScopes,
        );
      case "CompleteConnectionReauthorization":
        return this.completeAuthorization(command.authorizationAttemptId, command.providerCallbackRef);
      case "SetConnectionObservation":
        return this.setObservation(
          command.connectionId,
          command.expectedConnectionVersion,
          command.observationEnabled,
        );
      case "SetWriteAction":
        return this.setWriteAction(
          command.connectionId,
          command.expectedConnectionVersion,
          command.action,
          command.enabled,
        );
      case "GrantHostedDisclosure":
        return this.grantDisclosure(command);
      case "RevokeHostedDisclosure":
        return this.revokeDisclosure(command.grantId, command.expectedGrantVersion);
      case "RevokeConnectionCredentials":
        return this.revokeCredentials(command.connectionId, command.expectedConnectionVersion);
      case "DisconnectConnection":
        return this.disconnect(command.connectionId, command.expectedConnectionVersion);
      case "DeleteImportedConnectionContent":
        return this.deleteImported(
          command.connectionId,
          command.expectedConnectionVersion,
          command.confirmationToken,
        );
      case "ActivateReflex":
        return this.setReflex(command.reflex, command.expectedStateVersion, "active");
      case "PauseReflex":
        return this.setReflex(command.reflex, command.expectedStateVersion, "paused");
      case "RollbackReflex":
        return this.rollbackReflex(command.reflex, command.expectedStateVersion);
      default:
        return null;
    }
  }

  private async approve(
    operationId: string,
    expectedCanonicalHash: string,
    expectedCaseVersion: number,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const projection = await this.deps.authority.project();
    const operation = projection.operations.find((op) => op.operationId === operationId);
    if (!operation) return { ok: false, summary: "operation_not_found", error: "operation_not_found" };
    const connection = projection.connections.find((c) => c.connectionId === operation.connectionId);
    const pending = this.deps.authority.toPendingApproval(operation, connection);
    if (!pending) return { ok: false, summary: "connection_missing", error: "connection_missing" };
    if (connection && connection.connectionVersion !== operation.connectionVersion) {
      return { ok: false, summary: "connection_version_mismatch", error: "connection_version_mismatch" };
    }
    const scope = scopeStillGranted(pending, pending.grantedScopeKeys);
    if (!scope.ok) return { ok: false, summary: scope.error, error: scope.error };
    const result = tryApprove(pending, {
      type: "ApproveOperation",
      operationId,
      expectedCanonicalHash,
      expectedCaseVersion,
    });
    if (!result.ok) return { ok: false, summary: result.error, error: result.error };
    await this.deps.authority.setOperationStatus(operationId, "approved", at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "operation_approved", operationId };
  }

  private async reject(
    operationId: string,
    expectedCanonicalHash: string,
    expectedCaseVersion: number,
    reason?: string,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const projection = await this.deps.authority.project();
    const operation = projection.operations.find((op) => op.operationId === operationId);
    if (!operation) return { ok: false, summary: "operation_not_found", error: "operation_not_found" };
    const connection = projection.connections.find((c) => c.connectionId === operation.connectionId);
    const pending = this.deps.authority.toPendingApproval(operation, connection);
    if (!pending) return { ok: false, summary: "connection_missing", error: "connection_missing" };
    const result = tryReject(pending, {
      type: "RejectOperation",
      operationId,
      expectedCanonicalHash,
      expectedCaseVersion,
      ...(reason ? { reason } : {}),
    });
    if (!result.ok) return { ok: false, summary: result.error, error: result.error };
    await this.deps.authority.setOperationStatus(operationId, "rejected", at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "operation_rejected", operationId };
  }

  private async startAuthorization(
    connectorId: string,
    connectorVersion: number,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connectionId = this.deps.ids.next("conn");
    const authorizationAttemptId = this.deps.ids.next("auth");
    const connection: ConnectionRecord = {
      connectionId,
      connectionVersion: 1,
      connector: { id: connectorId, version: connectorVersion },
      connected: false,
      observationEnabled: false,
      healthStatus: "authorizing",
      selectedResources: [],
      writeActionEnabled: {},
      grantedOAuthScopes: [],
      readScopes: [],
    };
    const attempt: AuthAttemptRecord = {
      authorizationAttemptId,
      connectionId,
      connector: connection.connector,
      kind: "authorize",
      status: "pending",
      additionalOAuthScopes: [],
    };
    await this.deps.authority.upsertConnection(connection, at);
    await this.deps.authority.putAuthAttempt(attempt, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "authorization_started", connectionId, authorizationAttemptId };
  }

  private async completeAuthorization(
    authorizationAttemptId: string,
    providerCallbackRef: string,
  ): Promise<RelayCommandResult> {
    void providerCallbackRef;
    const at = this.deps.clock.now().toISOString();
    const projection = await this.deps.authority.project();
    const attempt = projection.authAttempts.find((a) => a.authorizationAttemptId === authorizationAttemptId);
    if (!attempt) return { ok: false, summary: "attempt_not_found", error: "attempt_not_found" };
    const connection = projection.connections.find((c) => c.connectionId === attempt.connectionId);
    if (!connection) return { ok: false, summary: "connection_missing", error: "connection_missing" };
    if (attempt.expectedConnectionVersion != null) {
      const version = tryMutateConnection(connection.connectionVersion, attempt.expectedConnectionVersion);
      if (!version.ok) return { ok: false, summary: version.error, error: version.error };
    }
    const nextScopes = unique([
      ...connection.grantedOAuthScopes,
      ...attempt.additionalOAuthScopes,
      `${connection.connector.id}.read`,
    ]);
    const next: ConnectionRecord = {
      ...connection,
      connectionVersion: connection.connectionVersion + 1,
      connected: true,
      healthStatus: "connected",
      grantedOAuthScopes: nextScopes,
      readScopes: unique([...connection.readScopes, `${connection.connector.id}.read`]),
    };
    await this.deps.authority.upsertConnection(next, at);
    await this.deps.authority.putAuthAttempt({ ...attempt, status: "completed" }, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "authorization_completed", connectionId: next.connectionId };
  }

  private async discoverResources(
    connectionId: string,
    expectedConnectionVersion: number,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    const next: ConnectionRecord = {
      ...connection.row,
      connectionVersion: connection.row.connectionVersion + 1,
      healthStatus: "discovered",
    };
    await this.deps.authority.upsertConnection(next, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "resources_discovered", connectionId };
  }

  private async selectResources(
    connectionId: string,
    expectedConnectionVersion: number,
    selectedResourceIds: readonly string[],
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    const next: ConnectionRecord = {
      ...connection.row,
      connectionVersion: connection.row.connectionVersion + 1,
      selectedResources: [...selectedResourceIds],
    };
    await this.deps.authority.upsertConnection(next, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "resources_selected", connectionId };
  }

  private async startReauthorization(
    connectionId: string,
    expectedConnectionVersion: number,
    additionalOAuthScopes: readonly string[],
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    const authorizationAttemptId = this.deps.ids.next("auth");
    const attempt: AuthAttemptRecord = {
      authorizationAttemptId,
      connectionId,
      connector: connection.row.connector,
      kind: "reauthorize",
      status: "pending",
      additionalOAuthScopes: [...additionalOAuthScopes],
      expectedConnectionVersion,
    };
    await this.deps.authority.putAuthAttempt(attempt, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "reauthorization_started", connectionId, authorizationAttemptId };
  }

  private async setObservation(
    connectionId: string,
    expectedConnectionVersion: number,
    observationEnabled: boolean,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    const next: ConnectionRecord = {
      ...connection.row,
      connectionVersion: connection.row.connectionVersion + 1,
      observationEnabled,
    };
    await this.deps.authority.upsertConnection(next, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: observationEnabled ? "observation_on" : "observation_off", connectionId };
  }

  private async setWriteAction(
    connectionId: string,
    expectedConnectionVersion: number,
    action: ConnectorActionRef,
    enabled: boolean,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    const key = formatActionKey(action);
    const next: ConnectionRecord = {
      ...connection.row,
      connectionVersion: connection.row.connectionVersion + 1,
      writeActionEnabled: { ...connection.row.writeActionEnabled, [key]: enabled },
    };
    await this.deps.authority.upsertConnection(next, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: enabled ? "write_action_enabled" : "write_action_disabled", connectionId };
  }

  private async grantDisclosure(command: {
    readonly connectionId: string;
    readonly expectedConnectionVersion: number;
    readonly disclosure: "hosted_session" | "hosted_project" | "public";
    readonly sensitivity: number;
    readonly purpose: string;
    readonly ttlMs?: number;
  }): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connection = await this.requireConnection(command.connectionId, command.expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    if (command.disclosure === "public" && connection.row.connector.id !== "public-search") {
      return {
        ok: false,
        summary: "disclosure_not_allowed",
        error: "disclosure_not_allowed",
      };
    }
    const grantId = this.deps.ids.next("grant");
    const grant: DisclosureGrantRecord = {
      grantId,
      grantVersion: 1,
      connectionId: command.connectionId,
      disclosure: command.disclosure,
      sensitivity: command.sensitivity,
      purpose: command.purpose,
      ...(command.ttlMs
        ? { expiresAt: new Date(this.deps.clock.now().getTime() + command.ttlMs).toISOString() }
        : {}),
    };
    await this.deps.authority.grantDisclosure(grant, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "disclosure_granted", connectionId: command.connectionId };
  }

  private async revokeDisclosure(
    grantId: string,
    expectedGrantVersion: number,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const projection = await this.deps.authority.project();
    const grant = projection.disclosures.find((g) => g.grantId === grantId);
    if (!grant) return { ok: false, summary: "grant_not_found", error: "grant_not_found" };
    if (grant.grantVersion !== expectedGrantVersion) {
      return { ok: false, summary: "grant_version_mismatch", error: "grant_version_mismatch" };
    }
    await this.deps.authority.revokeDisclosure(grantId, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "disclosure_revoked" };
  }

  private async revokeCredentials(
    connectionId: string,
    expectedConnectionVersion: number,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    const next: ConnectionRecord = {
      ...connection.row,
      connectionVersion: connection.row.connectionVersion + 1,
      grantedOAuthScopes: [],
      connected: false,
      healthStatus: "credentials_revoked",
    };
    await this.deps.authority.upsertConnection(next, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "credentials_revoked", connectionId };
  }

  private async disconnect(
    connectionId: string,
    expectedConnectionVersion: number,
  ): Promise<RelayCommandResult> {
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    const at = this.deps.clock.now().toISOString();
    await this.deps.authority.removeConnection(connectionId, at);
    await this.deps.emitSnapshot();
    return { ok: true, summary: "connection_disconnected", connectionId };
  }

  private async deleteImported(
    connectionId: string,
    expectedConnectionVersion: number,
    confirmationToken: string,
  ): Promise<RelayCommandResult> {
    if (confirmationToken !== `delete:${connectionId}`) {
      return { ok: false, summary: "confirmation_required", error: "confirmation_required" };
    }
    const connection = await this.requireConnection(connectionId, expectedConnectionVersion);
    if (!connection.ok) return connection.result;
    await this.deps.emitSnapshot();
    return { ok: true, summary: "imported_content_deleted", connectionId };
  }

  private async setReflex(
    reflex: { readonly id: string; readonly version: number },
    expectedStateVersion: number,
    activation: "active" | "paused",
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const projection = await this.deps.authority.project();
    const key = `${reflex.id}@${reflex.version}`;
    const current = projection.reflexes.find((r) => `${r.reflex.id}@${r.reflex.version}` === key);
    const stateVersion = current?.stateVersion ?? 0;
    const version = tryMutateReflex(stateVersion, expectedStateVersion);
    if (!version.ok) return { ok: false, summary: version.error, error: version.error };

    if (activation === "active" && this.deps.patterns) {
      const gate = await this.deps.patterns.canActivateBuiltReflex(reflex.id, reflex.version);
      if (!gate.ok) return { ok: false, summary: gate.summary, error: gate.summary };
    }

    await this.deps.authority.setReflexState(
      {
        reflex,
        stateVersion: stateVersion + 1,
        activation,
        runs: current?.runs ?? { runCount: 0, successCount: 0, failureCount: 0 },
      },
      at,
    );
    await this.deps.patterns?.markCandidateActivation(reflex.id, reflex.version, activation);
    await this.deps.emitSnapshot();
    return { ok: true, summary: activation === "active" ? "reflex_activated" : "reflex_paused" };
  }

  private async rollbackReflex(
    reflex: { readonly id: string; readonly version: number },
    expectedStateVersion: number,
  ): Promise<RelayCommandResult> {
    const at = this.deps.clock.now().toISOString();
    const projection = await this.deps.authority.project();
    const key = `${reflex.id}@${reflex.version}`;
    const current = projection.reflexes.find((r) => `${r.reflex.id}@${r.reflex.version}` === key);
    const prior = current?.activation ?? "inactive";
    // Soft-disable only after an activated or paused reflex — never brick activation_ready.
    if (prior !== "active" && prior !== "paused") {
      return { ok: false, summary: "nothing_to_rollback", error: "nothing_to_rollback" };
    }
    const stateVersion = current?.stateVersion ?? 0;
    const version = tryMutateReflex(stateVersion, expectedStateVersion);
    if (!version.ok) return { ok: false, summary: version.error, error: version.error };
    await this.deps.authority.setReflexState(
      {
        reflex,
        stateVersion: stateVersion + 1,
        activation: "inactive",
        runs: current?.runs ?? { runCount: 0, successCount: 0, failureCount: 0 },
      },
      at,
    );
    await this.deps.patterns?.markCandidateActivation(reflex.id, reflex.version, "rolled_back");
    await this.deps.emitSnapshot();
    return { ok: true, summary: "reflex_rolled_back" };
  }

  private async requireConnection(
    connectionId: string,
    expectedConnectionVersion: number,
  ): Promise<
    | { readonly ok: true; readonly row: ConnectionRecord }
    | { readonly ok: false; readonly result: RelayCommandResult }
  > {
    const projection = await this.deps.authority.project();
    const row = projection.connections.find((c) => c.connectionId === connectionId);
    if (!row) {
      return { ok: false, result: { ok: false, summary: "connection_missing", error: "connection_missing" } };
    }
    const version = tryMutateConnection(row.connectionVersion, expectedConnectionVersion);
    if (!version.ok) {
      return { ok: false, result: { ok: false, summary: version.error, error: version.error } };
    }
    return { ok: true, row };
  }
}

function unique(values: readonly string[]): string[] {
  return [...new Set(values)];
}
