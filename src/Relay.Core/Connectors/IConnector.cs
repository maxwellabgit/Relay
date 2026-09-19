namespace Relay.Core.Connectors;

public sealed record ConnectorDefinition(
    string Id,
    int Version,
    IReadOnlyList<string> ReadScopes,
    IReadOnlyList<string> WriteActionIds);

public sealed record Connection(
    string ConnectionId,
    string ConnectorId,
    bool Connected,
    bool ObservationEnabled,
    IReadOnlyList<string> SelectedResources,
    IReadOnlyList<string> ReadScopes,
    IReadOnlyDictionary<string, bool> WriteActionEnabled);

public sealed record ConnectorCursor(string Value);

public sealed record ConnectionHealth(bool Ok, string Status);

public sealed record ObservationPage(IReadOnlyList<string> ObjectIds, ConnectorCursor? NextCursor);

public sealed record ReadRequest(string ConnectionId, string ActionId, IReadOnlyDictionary<string, string> Arguments);

public sealed record ReadResult(bool Ok, IReadOnlyList<string> ObjectIds, string? Error);

public sealed record ApprovedOperation(string OperationId, string ActionId, string IdempotencyKey);

public sealed record OperationReceipt(bool Ok, string? ExternalId, string? Error);

public sealed record ExecutingOperation(string OperationId, string ActionId);

public sealed record OperationReconciliation(bool Completed, bool Duplicate, string? ExternalId, string? Error);

public interface IConnector
{
    ConnectorDefinition Definition { get; }
    Task<ConnectionHealth> CheckAsync(Connection connection, CancellationToken cancellationToken);
    Task<ObservationPage> ObserveAsync(Connection connection, ConnectorCursor? cursor, CancellationToken cancellationToken);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken cancellationToken);
    Task<OperationReceipt> ExecuteAsync(ApprovedOperation operation, CancellationToken cancellationToken);
    Task<OperationReconciliation> ReconcileAsync(ExecutingOperation operation, CancellationToken cancellationToken);
}
