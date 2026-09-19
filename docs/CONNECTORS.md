# Connectors

A connector authenticates to one service and exposes typed observation, read, and write operations. It does not decide which Reflex runs and it does not call Jev.

Every connection starts read-only. Write access is enabled per action. There is no connector-wide “Allow writes” switch.

Raw observed content is stored with **local-only** disclosure by default. A separate hosted-disclosure grant is required before that content may be sent to Jev.

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

## Identifier convention

Exact references use typed records, displayed as `@version`:

```text
ConnectorRef: google-calendar@1
ConnectorActionRef: google-calendar@1/google-calendar.event-create@1
```

Do not parse `@` out of free-form strings in code paths.

## Alpha set

| Connector | Default observation / read | Writes, each disabled by default |
| --- | --- | --- |
| Local conversation | Active only while Ask or Listen is on | Save a local note |
| Local RELAY memory | Read and search | Create or supersede a note; remember an acronym |
| Google Calendar | User-selected calendars | Create or update an event; no delete |
| Gmail | User-selected labels | Create a draft; no send, delete, or archive |
| Google Sheets | User-selected spreadsheets and ranges | Append or upsert rows; no sheet deletion |
| GitHub | User-selected repositories (fine-grained read) | Local draft (no GitHub write); create issue / comment after deliberate write reauthorization |
| Public web / Wikipedia | Search and fetch | None |
| Plaid | User-selected accounts | None |

GitHub observation and reads request fine-grained read permissions (`metadata`, `contents`, `issues`, `pull_requests`). Classic `repo` is not used. Write permission is requested only during reauthorization for a specific enabled action.

## Contract

Connectors return normalized `ObservedItemDraft` values. The runtime persists objects, source events, cursors, and work items. Write and reconcile requests carry connection, arguments, scopes, canonical hash, attempt ID, and source references—implementations must not recover that context from global repositories.

```csharp
public interface IConnector
{
    ConnectorDefinition Definition { get; }
    Task<ConnectionHealth> CheckAsync(Connection connection, CancellationToken ct);
    Task<ObservationPage> ObserveAsync(Connection connection, ConnectorCursor? cursor, CancellationToken ct);
    Task<IReadOnlyList<SelectableResource>> ListSelectableResourcesAsync(Connection connection, CancellationToken ct);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct);
    Task<OperationReceipt> ExecuteAsync(ConnectorWriteRequest request, CancellationToken ct);
    Task<OperationReconciliation> ReconcileAsync(ConnectorReconcileRequest request, CancellationToken ct);
}
```

Authorization start/complete, resource discovery/selection, reauthorization, disclosure grant/revoke, credential revocation, disconnect, and imported-content deletion are separate application commands. OAuth tokens never pass through UI commands or `Connection` records.

`ConnectorDefinition` lists versioned operations with observation/read/write kind, versioned JSON schemas, OAuth scopes, risk class, returned `DataPolicy`, idempotency, reconciliation, default enablement, resource scoping, and rate limits.
