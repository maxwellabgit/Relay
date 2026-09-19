import type { ConnectorActionRef } from "./artifacts.js";
import type { ApproveOperationCommand, RejectOperationCommand } from "./commands.js";

export type PendingOperationApproval = {
  readonly operationId: string;
  readonly action: ConnectorActionRef;
  readonly canonicalHash: string;
  readonly caseVersion: number;
  readonly connectionId: string;
  readonly connectionVersion: number;
  readonly grantedScopeKeys: ReadonlySet<string>;
  readonly writeActionEnabled: boolean;
  readonly boundActionVersion: number;
};

export type AuthorityResult = { readonly ok: true } | { readonly ok: false; readonly error: string };

export function tryApprove(
  pending: PendingOperationApproval,
  cmd: ApproveOperationCommand,
): AuthorityResult {
  if (pending.operationId !== cmd.operationId) return { ok: false, error: "operation_id_mismatch" };
  if (pending.canonicalHash !== cmd.expectedCanonicalHash) {
    return { ok: false, error: "canonical_hash_mismatch" };
  }
  if (pending.caseVersion !== cmd.expectedCaseVersion) {
    return { ok: false, error: "case_version_mismatch" };
  }
  if (!pending.writeActionEnabled) return { ok: false, error: "write_action_disabled" };
  if (pending.boundActionVersion !== pending.action.actionVersion) {
    return { ok: false, error: "action_version_mismatch" };
  }
  return { ok: true };
}

export function tryReject(
  pending: PendingOperationApproval,
  cmd: RejectOperationCommand,
): AuthorityResult {
  if (pending.operationId !== cmd.operationId) return { ok: false, error: "operation_id_mismatch" };
  if (pending.canonicalHash !== cmd.expectedCanonicalHash) {
    return { ok: false, error: "canonical_hash_mismatch" };
  }
  if (pending.caseVersion !== cmd.expectedCaseVersion) {
    return { ok: false, error: "case_version_mismatch" };
  }
  return { ok: true };
}

export function tryMutateConnection(
  currentVersion: number,
  expectedVersion: number,
): AuthorityResult {
  if (currentVersion !== expectedVersion) {
    return { ok: false, error: "connection_version_mismatch" };
  }
  return { ok: true };
}

export function tryMutateReflex(
  currentStateVersion: number,
  expectedStateVersion: number,
): AuthorityResult {
  if (currentStateVersion !== expectedStateVersion) {
    return { ok: false, error: "reflex_state_version_mismatch" };
  }
  return { ok: true };
}

export function scopeStillGranted(
  pending: PendingOperationApproval,
  currentGrantedScopeKeys: ReadonlySet<string>,
): AuthorityResult {
  for (const key of pending.grantedScopeKeys) {
    if (!currentGrantedScopeKeys.has(key)) {
      return { ok: false, error: "granted_scope_revoked" };
    }
  }
  return { ok: true };
}
