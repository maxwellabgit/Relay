using Microsoft.Data.Sqlite;
using Relay.Core.Storage;

namespace Relay.Core.Cases;

/// <summary>
/// Rebuildable SQLite projection of cases, operations, ready queue, feed items, and approvals.
/// Authoritative state remains in files; this database may be deleted and rebuilt.
/// </summary>
public sealed class ProjectionDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public ProjectionDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        EnsureSchema();
    }

    public static ProjectionDatabase Open(DataRoot root)
        => new(root.ProjectionsDatabasePath);

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS cases (
              case_id TEXT PRIMARY KEY,
              version INTEGER NOT NULL,
              origin TEXT NOT NULL,
              kind TEXT NOT NULL,
              status TEXT NOT NULL,
              approved_objective TEXT,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS operations (
              operation_id TEXT PRIMARY KEY,
              case_id TEXT NOT NULL,
              case_version INTEGER NOT NULL,
              capability TEXT NOT NULL,
              status TEXT NOT NULL,
              idempotency_key TEXT NOT NULL,
              approval_id TEXT,
              canonical_hash TEXT
            );
            CREATE TABLE IF NOT EXISTS ready_queue (
              case_id TEXT PRIMARY KEY,
              priority INTEGER NOT NULL,
              enqueued_at TEXT NOT NULL,
              lease_owner TEXT,
              lease_until TEXT
            );
            CREATE TABLE IF NOT EXISTS feed_items (
              feed_id TEXT PRIMARY KEY,
              case_id TEXT,
              ts TEXT NOT NULL,
              text TEXT NOT NULL,
              level TEXT
            );
            CREATE TABLE IF NOT EXISTS approvals (
              approval_id TEXT PRIMARY KEY,
              operation_id TEXT NOT NULL,
              case_id TEXT NOT NULL,
              envelope_hash TEXT NOT NULL,
              decided_at TEXT NOT NULL,
              decision TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertCase(CaseRecord record)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO cases(case_id, version, origin, kind, status, approved_objective, updated_at)
                VALUES($id, $ver, $origin, $kind, $status, $obj, $ts)
                ON CONFLICT(case_id) DO UPDATE SET
                  version=excluded.version,
                  status=excluded.status,
                  approved_objective=excluded.approved_objective,
                  updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$ver", record.Version);
            cmd.Parameters.AddWithValue("$origin", record.Origin);
            cmd.Parameters.AddWithValue("$kind", record.Kind);
            cmd.Parameters.AddWithValue("$status", record.Status);
            cmd.Parameters.AddWithValue("$obj", (object?)record.ApprovedObjective ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", record.UpdatedAt.UtcDateTime.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertOperation(OperationEnvelope envelope)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO operations(operation_id, case_id, case_version, capability, status, idempotency_key, approval_id, canonical_hash)
                VALUES($id, $case, $ver, $cap, $status, $key, $approval, $hash)
                ON CONFLICT(operation_id) DO UPDATE SET
                  case_version=excluded.case_version,
                  status=excluded.status,
                  approval_id=excluded.approval_id,
                  canonical_hash=excluded.canonical_hash;
                """;
            cmd.Parameters.AddWithValue("$id", envelope.OperationId);
            cmd.Parameters.AddWithValue("$case", envelope.CaseId);
            cmd.Parameters.AddWithValue("$ver", envelope.CaseVersion);
            cmd.Parameters.AddWithValue("$cap", envelope.Capability);
            cmd.Parameters.AddWithValue("$status", envelope.Status);
            cmd.Parameters.AddWithValue("$key", envelope.IdempotencyKey);
            cmd.Parameters.AddWithValue("$approval", (object?)envelope.ApprovalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", (object?)envelope.CanonicalHashValue ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertApproval(string approvalId, string operationId, string caseId, string envelopeHash, DateTimeOffset decidedAt, string decision)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO approvals(approval_id, operation_id, case_id, envelope_hash, decided_at, decision)
                VALUES($id, $op, $case, $hash, $ts, $decision)
                ON CONFLICT(approval_id) DO UPDATE SET decision=excluded.decision;
                """;
            cmd.Parameters.AddWithValue("$id", approvalId);
            cmd.Parameters.AddWithValue("$op", operationId);
            cmd.Parameters.AddWithValue("$case", caseId);
            cmd.Parameters.AddWithValue("$hash", envelopeHash);
            cmd.Parameters.AddWithValue("$ts", decidedAt.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$decision", decision);
            cmd.ExecuteNonQuery();
        }
    }

    public void InsertFeedItem(string feedId, string? caseId, DateTimeOffset ts, string text, string? level = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO feed_items(feed_id, case_id, ts, text, level)
                VALUES($id, $case, $ts, $text, $level);
                """;
            cmd.Parameters.AddWithValue("$id", feedId);
            cmd.Parameters.AddWithValue("$case", (object?)caseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", ts.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$level", (object?)level ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FeedItemRow> ListFeedItems(string? caseId = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            if (caseId is null)
            {
                cmd.CommandText = "SELECT feed_id, case_id, ts, text, level FROM feed_items ORDER BY ts ASC;";
            }
            else
            {
                cmd.CommandText = "SELECT feed_id, case_id, ts, text, level FROM feed_items WHERE case_id=$case ORDER BY ts ASC;";
                cmd.Parameters.AddWithValue("$case", caseId);
            }
            var list = new List<FeedItemRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new FeedItemRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return list;
        }
    }

    /// <summary>Rebuilds projections from on-disk case and operation stores.</summary>
    public void RebuildFromStores(CaseStore cases, OperationStore operations)
    {
        lock (_gate)
        {
            using (var clear = _connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM cases; DELETE FROM operations; DELETE FROM approvals; DELETE FROM feed_items;";
                clear.ExecuteNonQuery();
            }

            foreach (var id in cases.ListCaseIds())
            {
                var record = cases.TryLoadRecord(id);
                if (record is not null) UpsertCase(record);
            }

            foreach (var op in operations.ListAll())
                UpsertOperation(op);
        }
    }

    internal SqliteConnection Connection => _connection;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}

public sealed record FeedItemRow(string FeedId, string? CaseId, DateTimeOffset Ts, string Text, string? Level);
