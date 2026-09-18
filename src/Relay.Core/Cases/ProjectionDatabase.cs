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
            CREATE TABLE IF NOT EXISTS judgments (
              judgment_id TEXT PRIMARY KEY,
              case_id TEXT,
              case_version INTEGER,
              question_set_id TEXT NOT NULL,
              question_set_version TEXT NOT NULL,
              model TEXT NOT NULL,
              status TEXT NOT NULL,
              request_hash TEXT,
              request_object_id TEXT,
              response_object_id TEXT,
              response_hash TEXT,
              failure_category TEXT,
              input_tokens INTEGER,
              output_tokens INTEGER,
              elapsed_ms INTEGER,
              created_at TEXT NOT NULL,
              completed_at TEXT
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

    public void UpsertJudgment(Judgments.JudgmentRecord record)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO judgments(
                  judgment_id, case_id, case_version, question_set_id, question_set_version, model, status,
                  request_hash, request_object_id, response_object_id, response_hash, failure_category,
                  input_tokens, output_tokens, elapsed_ms, created_at, completed_at)
                VALUES(
                  $id, $case, $ver, $qs, $qsv, $model, $status,
                  $reqHash, $reqObj, $respObj, $respHash, $fail,
                  $inTok, $outTok, $elapsed, $created, $completed)
                ON CONFLICT(judgment_id) DO UPDATE SET
                  case_id=excluded.case_id,
                  case_version=excluded.case_version,
                  status=excluded.status,
                  request_hash=excluded.request_hash,
                  request_object_id=excluded.request_object_id,
                  response_object_id=excluded.response_object_id,
                  response_hash=excluded.response_hash,
                  failure_category=excluded.failure_category,
                  input_tokens=excluded.input_tokens,
                  output_tokens=excluded.output_tokens,
                  elapsed_ms=excluded.elapsed_ms,
                  completed_at=excluded.completed_at;
                """;
            cmd.Parameters.AddWithValue("$id", record.JudgmentId);
            cmd.Parameters.AddWithValue("$case", (object?)record.CaseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", (object?)record.CaseVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$qs", record.QuestionSetId);
            cmd.Parameters.AddWithValue("$qsv", record.QuestionSetVersion);
            cmd.Parameters.AddWithValue("$model", record.Model);
            cmd.Parameters.AddWithValue("$status", record.Status);
            cmd.Parameters.AddWithValue("$reqHash", (object?)record.RequestHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reqObj", (object?)record.RequestObjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$respObj", (object?)record.ResponseObjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$respHash", (object?)record.ResponseHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fail", (object?)record.FailureCategory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$inTok", (object?)record.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outTok", (object?)record.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$elapsed", (object?)record.ElapsedMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", record.CreatedAt.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$completed", record.CompletedAt is { } c ? c.UtcDateTime.ToString("O") : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<JudgmentProjectionRow> ListJudgments()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT judgment_id, case_id, case_version, question_set_id, question_set_version, model, status,
                       request_hash, request_object_id, response_object_id, response_hash, failure_category,
                       input_tokens, output_tokens, elapsed_ms, created_at, completed_at
                FROM judgments ORDER BY created_at ASC;
                """;
            var list = new List<JudgmentProjectionRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new JudgmentProjectionRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    DateTimeOffset.Parse(reader.GetString(15)),
                    reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16))));
            }
            return list;
        }
    }

    /// <summary>Rebuilds projections from on-disk case, operation, and judgment stores.</summary>
    public void RebuildFromStores(CaseStore cases, OperationStore operations, Judgments.JudgmentStore? judgments = null)
    {
        lock (_gate)
        {
            using (var clear = _connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM cases; DELETE FROM operations; DELETE FROM approvals; DELETE FROM feed_items; DELETE FROM judgments;";
                clear.ExecuteNonQuery();
            }

            foreach (var id in cases.ListCaseIds())
            {
                var record = cases.TryLoadRecord(id);
                if (record is not null) UpsertCase(record);
            }

            foreach (var op in operations.ListAll())
                UpsertOperation(op);

            if (judgments is not null)
            {
                foreach (var judgment in judgments.ListAll())
                    UpsertJudgment(judgment);
            }
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

public sealed record JudgmentProjectionRow(
    string JudgmentId,
    string? CaseId,
    long? CaseVersion,
    string QuestionSetId,
    string QuestionSetVersion,
    string Model,
    string Status,
    string? RequestHash,
    string? RequestObjectId,
    string? ResponseObjectId,
    string? ResponseHash,
    string? FailureCategory,
    int? InputTokens,
    int? OutputTokens,
    long? ElapsedMs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

