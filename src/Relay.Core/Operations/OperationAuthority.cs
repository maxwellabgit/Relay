using Relay.Core.Artifacts;
using Relay.Core.Application;
using Relay.Core.Connectors;

namespace Relay.Core.Operations;

/// <summary>
/// Pending approval binding used by the operation broker / application facade.
/// Approvals must match operation id, canonical hash, and case version.
/// </summary>
public sealed record PendingOperationApproval(
    string OperationId,
    ConnectorActionRef Action,
    string CanonicalHash,
    long CaseVersion,
    string ConnectionId,
    long ConnectionVersion,
    IReadOnlySet<string> GrantedScopeKeys,
    bool WriteActionEnabled,
    int BoundActionVersion);

/// <summary>Pure authority checks for optimistic UI commands.</summary>
public static class OperationAuthority
{
    public static bool TryApprove(
        PendingOperationApproval pending,
        ApproveOperation command,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(command);

        if (!string.Equals(pending.OperationId, command.OperationId, StringComparison.Ordinal))
        {
            error = "operation_id_mismatch";
            return false;
        }

        if (!string.Equals(pending.CanonicalHash, command.ExpectedCanonicalHash, StringComparison.Ordinal))
        {
            error = "canonical_hash_mismatch";
            return false;
        }

        if (pending.CaseVersion != command.ExpectedCaseVersion)
        {
            error = "case_version_mismatch";
            return false;
        }

        if (!pending.WriteActionEnabled)
        {
            error = "write_action_disabled";
            return false;
        }

        if (pending.BoundActionVersion != pending.Action.ActionVersion)
        {
            error = "action_version_mismatch";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryReject(
        PendingOperationApproval pending,
        RejectOperation command,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(command);

        if (!string.Equals(pending.OperationId, command.OperationId, StringComparison.Ordinal))
        {
            error = "operation_id_mismatch";
            return false;
        }

        if (!string.Equals(pending.CanonicalHash, command.ExpectedCanonicalHash, StringComparison.Ordinal))
        {
            error = "canonical_hash_mismatch";
            return false;
        }

        if (pending.CaseVersion != command.ExpectedCaseVersion)
        {
            error = "case_version_mismatch";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryMutateConnection(
        long currentConnectionVersion,
        long expectedConnectionVersion,
        out string? error)
    {
        if (currentConnectionVersion != expectedConnectionVersion)
        {
            error = "connection_version_mismatch";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryMutateReflex(
        long currentStateVersion,
        long expectedStateVersion,
        out string? error)
    {
        if (currentStateVersion != expectedStateVersion)
        {
            error = "reflex_state_version_mismatch";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ScopeStillGranted(
        PendingOperationApproval pending,
        IReadOnlySet<string> currentGrantedScopeKeys,
        out string? error)
    {
        foreach (var key in pending.GrantedScopeKeys)
        {
            if (!currentGrantedScopeKeys.Contains(key))
            {
                error = "granted_scope_revoked";
                return false;
            }
        }

        error = null;
        return true;
    }
}
