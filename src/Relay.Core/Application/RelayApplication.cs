using Relay.Core.Connectors;
using Relay.Core.Reflexes;

namespace Relay.Core.Application;

public abstract record RelayCommand;

public sealed record SubmitText(string Text, string? CaseId = null) : RelayCommand;

public sealed record SetListening(bool Enabled) : RelayCommand;

public sealed record ApproveOperation(string OperationId) : RelayCommand;

public sealed record RejectOperation(string OperationId, string? Reason = null) : RelayCommand;

public sealed record SetConnectionObservation(string ConnectionId, bool ObservationEnabled) : RelayCommand;

public sealed record SetWriteAction(string ConnectionId, string ActionId, bool Enabled) : RelayCommand;

public sealed record ActivateReflex(string ReflexId, int ReflexVersion) : RelayCommand;

public sealed record PauseReflex(string ReflexId) : RelayCommand;

public sealed record DisconnectConnection(string ConnectionId, bool DeleteImportedContent) : RelayCommand;

public sealed record RelayCommandResult(
    bool Ok,
    string Summary,
    string? CaseId,
    string? OperationId,
    string? Error);

public sealed record FeedItemSnapshot(
    string ItemId,
    string Kind,
    string Summary,
    DateTimeOffset CreatedAt,
    string? CaseId);

public sealed record ApprovalSnapshot(
    string OperationId,
    string ActionId,
    string Summary,
    DateTimeOffset ProposedAt);

public sealed record ConnectionStateSnapshot(
    string ConnectionId,
    string ConnectorId,
    bool Connected,
    bool ObservationEnabled,
    string HealthStatus,
    IReadOnlyDictionary<string, bool> WriteActionEnabled);

public sealed record ReflexStateSnapshot(
    string ReflexId,
    int ReflexVersion,
    ReflexActivationState Activation,
    int RunCount);

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
