using System.Text.Json;
using Relay.Core.Artifacts;
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

/// <summary>Versioned JSON Schema identity for connector arguments or results.</summary>
public sealed record ArgumentSchemaRef(string SchemaId, int SchemaVersion)
{
    public string Display => $"{SchemaId}@{SchemaVersion}";
}

public sealed record JsonSchemaDocument(string SchemaId, int SchemaVersion, string JsonSchema);

/// <summary>Closed precondition set. Not a free-form expression language.</summary>
public abstract record OperationPrecondition;

public sealed record ProviderRevisionEquals(string ExpectedRevision) : OperationPrecondition;

public sealed record ResourceExists(string ResourceId) : OperationPrecondition;

public sealed record ResourceMissing(string ResourceId) : OperationPrecondition;

public sealed record FieldEquals(string FieldPath, string ExpectedValue) : OperationPrecondition;

public sealed record EquivalentCalendarEventAbsent(string PersonKey, string MonthDay) : OperationPrecondition;

/// <summary>
/// One versioned observation, read, or write operation exposed by a connector.
/// Implementations must not invent actions outside this definition list.
/// </summary>
public sealed record ConnectorOperationDefinition(
    string ActionId,
    int ActionVersion,
    ConnectorOperationKind Kind,
    DataPolicy InputPolicy,
    DataPolicy ReturnedPolicy,
    ArgumentSchemaRef InputSchema,
    ArgumentSchemaRef OutputSchema,
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
    RateLimitPolicy? DefaultRateLimit)
{
    public ConnectorRef Ref => new(Id, Version);

    public ConnectorActionRef ActionRef(string actionId, int actionVersion = 1) =>
        new(Id, Version, actionId, actionVersion);
}

/// <summary>
/// Connection state without credentials. OAuth tokens never appear here or in UI commands.
/// </summary>
public sealed record Connection(
    string ConnectionId,
    long ConnectionVersion,
    ConnectorRef Connector,
    bool Connected,
    bool ObservationEnabled,
    IReadOnlyList<string> SelectedResources,
    IReadOnlyList<string> ReadScopes,
    IReadOnlyDictionary<string, bool> WriteActionEnabled,
    IReadOnlyList<string> GrantedOAuthScopes);

public sealed record ConnectorCursor(string Value);

public sealed record ConnectionHealth(bool Ok, string Status);

public sealed record SelectableResource(
    string ResourceId,
    string DisplayName,
    string Kind,
    IReadOnlyDictionary<string, string> SafeMetadata);

/// <summary>
/// Normalized observation draft. The runtime persists objects, source events, cursors, and work items.
/// Connectors must not write to the object store themselves.
/// Raw observed content defaults to local-only disclosure; a separate grant authorizes hosted use.
/// </summary>
public sealed record ObservedItemDraft(
    string ProviderItemId,
    string ProviderRevision,
    DateTimeOffset OccurredAt,
    string ContentType,
    DataPolicy Policy,
    ReadOnlyMemory<byte> Content,
    IReadOnlyDictionary<string, string> SafeMetadata,
    bool Deleted);

public sealed record ObservationPage(
    IReadOnlyList<ObservedItemDraft> Items,
    ConnectorCursor? NextCursor);

public sealed record ReadRequest(
    string ConnectionId,
    ConnectorActionRef Action,
    JsonElement Arguments,
    IReadOnlyList<SourceSliceRef> InputRefs);

public sealed record ReadResult(
    bool Ok,
    IReadOnlyList<ObservedItemDraft> Items,
    string? Error);

/// <summary>
/// Complete write dispatch context. Connectors must not recover missing fields from global repositories.
/// </summary>
public sealed record ConnectorWriteRequest(
    string OperationId,
    string DispatchAttemptId,
    string ConnectionId,
    ConnectorActionRef Action,
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
    ConnectorActionRef Action,
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
    Task<IReadOnlyList<SelectableResource>> ListSelectableResourcesAsync(Connection connection, CancellationToken cancellationToken);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken cancellationToken);
    Task<OperationReceipt> ExecuteAsync(ConnectorWriteRequest request, CancellationToken cancellationToken);
    Task<OperationReconciliation> ReconcileAsync(ConnectorReconcileRequest request, CancellationToken cancellationToken);
}
