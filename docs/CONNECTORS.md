# Connectors

A connector authenticates to one service and exposes typed observation, read, and write operations. It does not decide which Reflex runs and it does not call Jev.

Every connection starts read-only. Write access is enabled per action. There is no connector-wide “Allow writes” switch.

## Independent controls

```text
connected
observationEnabled
selectedResources
readScopes
writeActionEnabled[actionId]
hostedDisclosureEnabled[purpose]
```

Alpha write states are `disabled` and `approval_required`. Enabling a write action permits a proposal only. The user still approves the resulting operation.

## Alpha set

| Connector | Default observation / read | Writes, each disabled by default |
| --- | --- | --- |
| Local conversation | Active only while Ask or Listen is on | Save a local note |
| Local RELAY memory | Read and search | Create or supersede a note; remember an acronym |
| Google Calendar | User-selected calendars | Create or update an event; no delete |
| Gmail | User-selected labels | Create a draft; no send, delete, or archive |
| Google Sheets | User-selected spreadsheets and ranges | Append or upsert rows; no sheet deletion |
| GitHub | User-selected repositories | Draft an issue or comment; publish stays approval-bound |
| Public web / Wikipedia | Search and fetch | None |
| Plaid | User-selected accounts | None |

## Contract

Connectors return normalized `ObservedItemDraft` values. The runtime persists objects, source events, cursors, and work items. Write and reconcile requests carry connection, arguments, scopes, canonical hash, attempt ID, and source references—implementations must not recover that context from global repositories.

```csharp
public interface IConnector
{
    ConnectorDefinition Definition { get; }
    Task<ConnectionHealth> CheckAsync(Connection connection, CancellationToken ct);
    Task<ObservationPage> ObserveAsync(Connection connection, ConnectorCursor? cursor, CancellationToken ct);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct);
    Task<OperationReceipt> ExecuteAsync(ConnectorWriteRequest request, CancellationToken ct);
    Task<OperationReconciliation> ReconcileAsync(ConnectorReconcileRequest request, CancellationToken ct);
}
```

`ConnectorDefinition` lists versioned operations with observation/read/write kind, schemas, OAuth scopes, risk class, returned classification, idempotency, reconciliation, default enablement, resource scoping, and rate limits. Do not implement providers against a bare list of string IDs.

Disconnecting a source revokes tokens and offers deletion of imported content and derived indexes. Removing one selected resource makes it immediately unavailable to search.
