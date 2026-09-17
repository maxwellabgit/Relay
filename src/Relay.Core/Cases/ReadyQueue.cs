using Microsoft.Data.Sqlite;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>Ready-queue priority bands. Lower number = higher priority.</summary>
public static class ReadyPriority
{
    public const int UserReplyOrApproval = 1;
    public const int NewDirectRequest = 2;
    public const int ToolOrDelegateCompletion = 3;
    public const int UrgentObserved = 4;
    public const int NormalObserved = 5;
    public const int Maintenance = 6;
}

public sealed record ReadyLease(string CaseId, int Priority, string LeaseOwner, DateTimeOffset LeaseUntil);

/// <summary>
/// Persistent ready queue backed by the projections SQLite database.
/// A case cannot be queued twice while already ready or leased.
/// </summary>
public sealed class ReadyQueue
{
    private readonly ProjectionDatabase _db;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    public ReadyQueue(ProjectionDatabase db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Enqueues a case at the given priority. No-op if already ready or leased.</summary>
    public bool TryEnqueue(string caseId, int priority)
    {
        lock (_gate)
        {
            using var existing = _db.Connection.CreateCommand();
            existing.CommandText = "SELECT case_id, lease_owner, lease_until FROM ready_queue WHERE case_id=$id;";
            existing.Parameters.AddWithValue("$id", caseId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var owner = reader.IsDBNull(1) ? null : reader.GetString(1);
                var untilText = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (owner is null) return false; // already ready, not leased
                if (untilText is not null && DateTimeOffset.TryParse(untilText, out var until) && until > _clock.UtcNow)
                    return false; // still leased
                // Expired lease — allow re-enqueue by replacing the row.
            }

            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO ready_queue(case_id, priority, enqueued_at, lease_owner, lease_until)
                VALUES($id, $pri, $ts, NULL, NULL)
                ON CONFLICT(case_id) DO UPDATE SET
                  priority=excluded.priority,
                  enqueued_at=excluded.enqueued_at,
                  lease_owner=NULL,
                  lease_until=NULL;
                """;
            cmd.Parameters.AddWithValue("$id", caseId);
            cmd.Parameters.AddWithValue("$pri", priority);
            cmd.Parameters.AddWithValue("$ts", _clock.UtcNow.UtcDateTime.ToString("O"));
            cmd.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>Dequeues the highest-priority ready item and takes a lease on it.</summary>
    public ReadyLease? TryDequeue(string leaseOwner, TimeSpan? leaseDuration = null)
    {
        lock (_gate)
        {
            ExpireLeases();

            using var pick = _db.Connection.CreateCommand();
            pick.CommandText =
                """
                SELECT case_id, priority FROM ready_queue
                WHERE lease_owner IS NULL
                ORDER BY priority ASC, enqueued_at ASC
                LIMIT 1;
                """;
            using var reader = pick.ExecuteReader();
            if (!reader.Read()) return null;
            var caseId = reader.GetString(0);
            var priority = reader.GetInt32(1);
            reader.Close();

            var until = _clock.UtcNow + (leaseDuration ?? DefaultLease);
            using var lease = _db.Connection.CreateCommand();
            lease.CommandText =
                """
                UPDATE ready_queue SET lease_owner=$owner, lease_until=$until
                WHERE case_id=$id AND lease_owner IS NULL;
                """;
            lease.Parameters.AddWithValue("$owner", leaseOwner);
            lease.Parameters.AddWithValue("$until", until.UtcDateTime.ToString("O"));
            lease.Parameters.AddWithValue("$id", caseId);
            if (lease.ExecuteNonQuery() == 0) return null;

            return new ReadyLease(caseId, priority, leaseOwner, until);
        }
    }

    /// <summary>Removes a case from the ready queue after work completes or it enters a wait.</summary>
    public void Complete(string caseId)
    {
        lock (_gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ready_queue WHERE case_id=$id;";
            cmd.Parameters.AddWithValue("$id", caseId);
            cmd.ExecuteNonQuery();
        }
    }

    public void ReleaseLease(string caseId)
    {
        lock (_gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "UPDATE ready_queue SET lease_owner=NULL, lease_until=NULL WHERE case_id=$id;";
            cmd.Parameters.AddWithValue("$id", caseId);
            cmd.ExecuteNonQuery();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _db.Connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM ready_queue;";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    public bool Contains(string caseId)
    {
        lock (_gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM ready_queue WHERE case_id=$id;";
            cmd.Parameters.AddWithValue("$id", caseId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    private void ExpireLeases()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE ready_queue SET lease_owner=NULL, lease_until=NULL
            WHERE lease_until IS NOT NULL AND lease_until < $now;
            """;
        cmd.Parameters.AddWithValue("$now", _clock.UtcNow.UtcDateTime.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
