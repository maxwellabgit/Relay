using Relay.Core.Artifacts;
using Relay.Core.Connectors;
using Relay.Core.Reflexes;
using Relay.Core.Security;

namespace Relay.Core.Application;

public abstract record RelayCommand;

public sealed record SubmitText(string Text, string? CaseId = null) : RelayCommand;

public sealed record SetListening(bool Enabled) : RelayCommand;

public sealed record ApproveOperation(
    string OperationId,
    string ExpectedCanonicalHash,
    long ExpectedCaseVersion) : RelayCommand;

public sealed record RejectOperation(
    string OperationId,
    string ExpectedCanonicalHash,
    long ExpectedCaseVersion,
    string? Reason = null) : RelayCommand;

public sealed record StartConnectionAuthorization(ConnectorRef Connector) : RelayCommand;

public sealed record CompleteConnectionAuthorization(
    string AuthorizationAttemptId,
    string ProviderCallbackRef) : RelayCommand;

public sealed record DiscoverConnectionResources(
    string ConnectionId,
    long ExpectedConnectionVersion) : RelayCommand;

public sealed record SelectConnectionResources(
    string ConnectionId,
    long ExpectedConnectionVersion,
    IReadOnlyList<string> SelectedResourceIds) : RelayCommand;

public sealed record StartConnectionReauthorization(
    string ConnectionId,
    long ExpectedConnectionVersion,
    IReadOnlyList<string> AdditionalOAuthScopes) : RelayCommand;

public sealed record CompleteConnectionReauthorization(
    string AuthorizationAttemptId,
    string ProviderCallbackRef) : RelayCommand;

public sealed record SetConnectionObservation(
    string ConnectionId,
    long ExpectedConnectionVersion,
    bool ObservationEnabled) : RelayCommand;

public sealed record SetWriteAction(
    string ConnectionId,
    long ExpectedConnectionVersion,
    ConnectorActionRef Action,
    bool Enabled) : RelayCommand;

public sealed record GrantHostedDisclosure(
    string ConnectionId,
    long ExpectedConnectionVersion,
    DisclosureClass Disclosure,
    DataSensitivity Sensitivity,
    string Purpose,
    TimeSpan? Ttl = null) : RelayCommand;

public sealed record RevokeHostedDisclosure(
    string GrantId,
    long ExpectedGrantVersion) : RelayCommand;

public sealed record RevokeConnectionCredentials(
    string ConnectionId,
    long ExpectedConnectionVersion) : RelayCommand;

public sealed record DisconnectConnection(
    string ConnectionId,
    long ExpectedConnectionVersion) : RelayCommand;

public sealed record DeleteImportedConnectionContent(
    string ConnectionId,
    long ExpectedConnectionVersion,
    string ConfirmationToken) : RelayCommand;

public sealed record ActivateReflex(
    ReflexRef Reflex,
    long ExpectedStateVersion) : RelayCommand;

public sealed record PauseReflex(
    ReflexRef Reflex,
    long ExpectedStateVersion) : RelayCommand;

public sealed record RelayCommandResult(
    bool Ok,
    string Summary,
    string? CaseId,
    string? OperationId,
    string? ConnectionId,
    string? AuthorizationAttemptId,
    string? Error);

public sealed record FeedItemSnapshot(
    string ItemId,
    string Kind,
    string Summary,
    DateTimeOffset CreatedAt,
    string? CaseId);

public sealed record ApprovalSnapshot(
    string OperationId,
    ConnectorActionRef Action,
    string Summary,
    string CanonicalHash,
    long CaseVersion,
    string ConnectionId,
    long ConnectionVersion,
    DateTimeOffset ProposedAt);

public sealed record ConnectionStateSnapshot(
    string ConnectionId,
    long ConnectionVersion,
    ConnectorRef Connector,
    bool Connected,
    bool ObservationEnabled,
    string HealthStatus,
    IReadOnlyList<string> SelectedResources,
    IReadOnlyDictionary<string, bool> WriteActionEnabled,
    IReadOnlyList<string> GrantedOAuthScopes);

public sealed record ReflexStateSnapshot(
    ReflexRef Reflex,
    long StateVersion,
    ReflexActivationState Activation,
    ReflexRunSummary Runs);

public sealed record ProviderHealthSnapshot(
    string ProviderId,
    bool Ok,
    string Status);

public sealed record WaitSnapshot(
    string CaseId,
    string WaitKind,
    DateTimeOffset? DueAt);

public sealed record RelaySnapshot(
    bool Listening,
    string? ActiveCaseId,
    IReadOnlyList<FeedItemSnapshot> FeedItems,
    IReadOnlyList<ApprovalSnapshot> Approvals,
    IReadOnlyList<ConnectionStateSnapshot> Connections,
    IReadOnlyList<ReflexStateSnapshot> Reflexes,
    IReadOnlyList<ProviderHealthSnapshot> ProviderHealth,
    IReadOnlyList<WaitSnapshot> Waits);

public abstract record RelayChange;

public sealed record SnapshotReplaced(RelaySnapshot Snapshot) : RelayChange;

public sealed record FeedItemAdded(FeedItemSnapshot Item) : RelayChange;

public sealed record ApprovalChanged(ApprovalSnapshot? Approval, string OperationId, bool Removed) : RelayChange;

public sealed record ListeningChanged(bool Listening) : RelayChange;

public sealed record ConnectionChanged(ConnectionStateSnapshot Connection) : RelayChange;

public sealed record ReflexChanged(ReflexStateSnapshot Reflex) : RelayChange;

/// <summary>
/// Single UI-facing facade. Commands are typed; the UI observes an asynchronous change stream.
/// OAuth tokens never pass through commands.
/// </summary>
public interface IRelayApplication
{
    Task<RelayCommandResult> ExecuteAsync(RelayCommand command, CancellationToken cancellationToken);
    Task<RelaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<RelayChange> WatchAsync(CancellationToken cancellationToken);
}

/// <summary>One application-owned background loop. The UI never pumps it.</summary>
public interface IRelayRuntimeService : IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken);
}
