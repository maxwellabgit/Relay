using Relay.Core.Artifacts;
using Relay.Core.Application;
using Relay.Core.Operations;

namespace Relay.Core.Tests;

public sealed class OperationAuthorityTests
{
    private static PendingOperationApproval Pending(
        string hash = "hash-a",
        long caseVersion = 3,
        bool writeEnabled = true,
        int actionVersion = 1,
        params string[] scopes) =>
        new(
            OperationId: "op-1",
            Action: new ConnectorActionRef("google-calendar", 1, "google-calendar.event-create", actionVersion),
            CanonicalHash: hash,
            CaseVersion: caseVersion,
            ConnectionId: "conn-1",
            ConnectionVersion: 2,
            GrantedScopeKeys: scopes.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) { "calendar:birthdays" } : scopes.ToHashSet(StringComparer.Ordinal),
            WriteActionEnabled: writeEnabled,
            BoundActionVersion: 1);

    [Fact]
    public void Approve_succeeds_when_hash_and_case_version_match()
    {
        var pending = Pending();
        Assert.True(OperationAuthority.TryApprove(pending, new ApproveOperation("op-1", "hash-a", 3), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Approve_fails_after_argument_edit_changes_hash()
    {
        var pending = Pending(hash: "hash-edited");
        Assert.False(OperationAuthority.TryApprove(pending, new ApproveOperation("op-1", "hash-a", 3), out var error));
        Assert.Equal("canonical_hash_mismatch", error);
    }

    [Fact]
    public void Approve_fails_after_case_version_change()
    {
        var pending = Pending(caseVersion: 4);
        Assert.False(OperationAuthority.TryApprove(pending, new ApproveOperation("op-1", "hash-a", 3), out var error));
        Assert.Equal("case_version_mismatch", error);
    }

    [Fact]
    public void Approve_fails_after_action_version_change()
    {
        var pending = Pending(actionVersion: 2);
        Assert.False(OperationAuthority.TryApprove(pending, new ApproveOperation("op-1", "hash-a", 3), out var error));
        Assert.Equal("action_version_mismatch", error);
    }

    [Fact]
    public void Approve_fails_when_write_toggle_disabled()
    {
        var pending = Pending(writeEnabled: false);
        Assert.False(OperationAuthority.TryApprove(pending, new ApproveOperation("op-1", "hash-a", 3), out var error));
        Assert.Equal("write_action_disabled", error);
    }

    [Fact]
    public void Approve_fails_when_granted_scope_revoked()
    {
        var pending = Pending();
        Assert.False(OperationAuthority.ScopeStillGranted(pending, new HashSet<string>(StringComparer.Ordinal), out var error));
        Assert.Equal("granted_scope_revoked", error);
    }

    [Fact]
    public void Reject_requires_hash_and_case_version()
    {
        var pending = Pending();
        Assert.False(OperationAuthority.TryReject(pending, new RejectOperation("op-1", "stale", 3), out var error));
        Assert.Equal("canonical_hash_mismatch", error);
    }

    [Fact]
    public void Connection_and_reflex_mutations_require_optimistic_versions()
    {
        Assert.False(OperationAuthority.TryMutateConnection(5, 4, out var connError));
        Assert.Equal("connection_version_mismatch", connError);
        Assert.True(OperationAuthority.TryMutateConnection(5, 5, out _));

        Assert.False(OperationAuthority.TryMutateReflex(2, 1, out var reflexError));
        Assert.Equal("reflex_state_version_mismatch", reflexError);
        Assert.True(OperationAuthority.TryMutateReflex(2, 2, out _));
    }
}
