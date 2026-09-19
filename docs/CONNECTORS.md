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

```csharp
public interface IConnector
{
    ConnectorDefinition Definition { get; }
    Task<ConnectionHealth> CheckAsync(Connection connection, CancellationToken ct);
    Task<ObservationPage> ObserveAsync(Connection connection, ConnectorCursor? cursor, CancellationToken ct);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct);
    Task<OperationReceipt> ExecuteAsync(ApprovedOperation operation, CancellationToken ct);
    Task<OperationReconciliation> ReconcileAsync(ExecutingOperation operation, CancellationToken ct);
}
```

Disconnecting a source revokes tokens and offers deletion of imported content and derived indexes. Removing one selected resource makes it immediately unavailable to search.
