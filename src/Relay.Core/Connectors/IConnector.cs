using System.Text.Json;
using Relay.Core.Security;
using Relay.Core.Sources;

namespace Relay.Core.Connectors;

public enum ConnectorOperationKind
{
    Observe,
    Read,
    Write,
}

public enum RiskClass
{
    None,
    Low,
    Medium,
    High,
}

public sealed record RateLimitPolicy(int MaxRequests, TimeSpan Window);

/// <summary>
/// One versioned observation, read, or write operation exposed by a connector.
/// Implementations must not invent actions outside this definition list.
/// </summary>
public sealed record ConnectorOperationDefinition(
    string ActionId,
    int ActionVersion,
    ConnectorOperationKind Kind,
    DataClassification InputClassification,
    DataClassification ReturnedDataClassification,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<string> RequiredOAuthScopes,
    RiskClass RiskClass,
    bool SupportsIdempotency,
    bool SupportsReconciliation,
    bool DefaultEnabled,
    bool RequiresResourceScope,
    RateLimitPolicy? RateLimit);

public sealed record ConnectorDefinition(
    string Id,
    int Version,
    string DisplayName,
    bool SupportsObservation,
    IReadOnlyList<string> DefaultReadScopes,
    IReadOnlyList<ConnectorOperationDefinition> Operations,
    RateLimitPolicy? DefaultRateLimit);

public sealed record Connection(
    string ConnectionId,
    string ConnectorId,
    int ConnectorVersion,
    bool Connected,
    bool ObservationEnabled,
    IReadOnlyList<string> SelectedResources,
    IReadOnlyList<string> ReadScopes,
    IReadOnlyDictionary<string, bool> WriteActionEnabled);

public sealed record ConnectorCursor(string Value);

public sealed record ConnectionHealth(bool Ok, string Status);

/// <summary>
/// Normalized observation draft. The runtime persists objects, source events, cursors, and work items.
/// Connectors must not write to the object store themselves.
/// </summary>
public sealed record ObservedItemDraft(
    string ProviderItemId,
    string ProviderRevision,
    DateTimeOffset OccurredAt,
    string ContentType,
    DataClassification Classification,
    ReadOnlyMemory<byte> Content,
    IReadOnlyDictionary<string, string> SafeMetadata,
    bool Deleted);

public sealed record ObservationPage(
    IReadOnlyList<ObservedItemDraft> Items,
    ConnectorCursor? NextCursor);

public sealed record ReadRequest(
    string ConnectionId,
    string ActionId,
    int ActionVersion,
    JsonElement Arguments,
    IReadOnlyList<SourceSliceRef> InputRefs);

public sealed record ReadResult(
    bool Ok,
    IReadOnlyList<ObservedItemDraft> Items,
    string? Error);

public sealed record OperationPrecondition(
    string Kind,
    string Expression);

/// <summary>
/// Complete write dispatch context. Connectors must not recover missing fields from global repositories.
/// </summary>
public sealed record ConnectorWriteRequest(
    string OperationId,
    string DispatchAttemptId,
    string ConnectionId,
    string ConnectorId,
    int ConnectorVersion,
    string ActionId,
    int ActionVersion,
    JsonElement Arguments,
    IReadOnlyList<SourceSliceRef> InputRefs,
    JsonElement GrantedScope,
    IReadOnlyList<OperationPrecondition> Preconditions,
    string CanonicalHash,
    string IdempotencyKey);

public sealed record OperationReceipt(
    bool Ok,
    string? ExternalId,
    string? ExternalRevision,
    string? Error);

public sealed record ConnectorReconcileRequest(
    string OperationId,
    string DispatchAttemptId,
    string ConnectionId,
    string ConnectorId,
    int ConnectorVersion,
    string ActionId,
    int ActionVersion,
    string IdempotencyKey,
    string? ExternalId,
    JsonElement DispatchMetadata);

public sealed record OperationReconciliation(
    bool Completed,
    bool Duplicate,
    string? ExternalId,
    string? ExternalRevision,
    string? Error);

public interface IConnector
{
    ConnectorDefinition Definition { get; }
    Task<ConnectionHealth> CheckAsync(Connection connection, CancellationToken cancellationToken);
    Task<ObservationPage> ObserveAsync(Connection connection, ConnectorCursor? cursor, CancellationToken cancellationToken);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken cancellationToken);
    Task<OperationReceipt> ExecuteAsync(ConnectorWriteRequest request, CancellationToken cancellationToken);
    Task<OperationReconciliation> ReconcileAsync(ConnectorReconcileRequest request, CancellationToken cancellationToken);
}
