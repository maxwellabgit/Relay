using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Cases;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Judgments;

/// <summary>
/// Persists judgment request/response bodies in the object store and metadata under
/// <c>judgments/</c>. Ledger/case events should only reference IDs and hashes from
/// <see cref="ToAuditPayload"/>.
/// </summary>
public sealed class JudgmentStore
{
    private readonly DataRoot _root;
    private readonly ObjectStore _objects;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public JudgmentStore(DataRoot root, ObjectStore objects, IClock clock)
    {
        _root = root;
        _objects = objects;
        _clock = clock;
    }

    public string JudgmentsDirectory => Path.Combine(_root.Path, "judgments");

    private string RecordPath(string judgmentId) => Path.Combine(JudgmentsDirectory, judgmentId + ".json");
    private string HashIndexPath(string requestHash) =>
        Path.Combine(JudgmentsDirectory, "by-hash", requestHash.ToLowerInvariant() + ".json");

    /// <summary>
    /// Persist the request before provider dispatch. Returns an existing completed judgment
    /// for the same request hash when present (idempotent cache hit).
    /// </summary>
    public JudgmentDispatchHandle BeginRequest(
        JudgmentRequest request,
        string provider,
        string? judgmentId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var questionHash = JudgmentRequestHasher.HashQuestions(request.Questions);
        var stateHash = JudgmentRequestHasher.HashJsonElement(request.State);
        var sourceHashes = request.SourceObjectRefs.Select(r => r.Sha256);
        var requestHash = string.IsNullOrWhiteSpace(request.RequestHash)
            ? JudgmentRequestHasher.Compute(
                provider,
                request.Model,
                request.QuestionSetId,
                request.QuestionSetVersion,
                questionHash,
                stateHash,
                sourceHashes)
            : request.RequestHash!;

        lock (_gate)
        {
            var cached = TryLoadCompletedByHash(requestHash);
            if (cached is not null)
                return new JudgmentDispatchHandle(cached, AlreadyComplete: true, Dispatched: false);

            var id = judgmentId ?? Ulid.NewUlid(_clock.UtcNow);
            var requestObject = _objects.PutJson(new
            {
                request,
                provider,
                questionDefinitionsHash = questionHash,
                stateHash,
                requestHash,
            }, objectId: id + "-request");

            var record = new JudgmentRecord
            {
                JudgmentId = id,
                Provider = provider,
                QuestionSetId = request.QuestionSetId,
                QuestionSetVersion = request.QuestionSetVersion,
                Model = request.Model,
                Status = JudgmentStatuses.Requested,
                CaseId = request.CaseId,
                CaseVersion = request.CaseVersion,
                RequestObjectId = requestObject.ObjectId,
                RequestHash = requestHash,
                CreatedAt = _clock.UtcNow,
            };

            SaveRecord(record);
            WriteHashIndex(requestHash, id, JudgmentStatuses.Requested);
            return new JudgmentDispatchHandle(record, AlreadyComplete: false, Dispatched: false);
        }
    }

    /// <summary>Persist a successful response before any decision application.</summary>
    public JudgmentRecord CompleteSuccess(string judgmentId, JudgmentSuccess success, string? responseHash = null)
    {
        ArgumentNullException.ThrowIfNull(success);
        success.Validate();

        lock (_gate)
        {
            var record = LoadRequired(judgmentId);
            if (record.Status == JudgmentStatuses.Completed && record.ResponseObjectId is not null)
                return record;

            var responseObject = _objects.PutJson(success, objectId: judgmentId + "-response");
            var updated = record with
            {
                Status = JudgmentStatuses.Completed,
                Model = success.Model,
                ResponseObjectId = responseObject.ObjectId,
                ResponseHash = responseHash ?? responseObject.Sha256,
                InputTokens = success.InputTokens,
                OutputTokens = success.OutputTokens,
                ElapsedMs = success.ElapsedMs,
                CompletedAt = _clock.UtcNow,
                FailureCategory = null,
            };
            SaveRecord(updated);
            if (updated.RequestHash is not null)
                WriteHashIndex(updated.RequestHash, updated.JudgmentId, JudgmentStatuses.Completed);
            return updated;
        }
    }

    public JudgmentRecord CompleteFailure(string judgmentId, JudgmentFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        failure.Validate();

        lock (_gate)
        {
            var record = LoadRequired(judgmentId);
            var failureObject = _objects.PutJson(failure, objectId: judgmentId + "-failure");
            var updated = record with
            {
                Status = JudgmentStatuses.Failed,
                ResponseObjectId = failureObject.ObjectId,
                ResponseHash = failureObject.Sha256,
                FailureCategory = failure.Category,
                CompletedAt = _clock.UtcNow,
            };
            SaveRecord(updated);
            // Failed judgments are not cached as successful results — leave hash index as requested
            // or point at failed id without treating it as a cache hit.
            if (updated.RequestHash is not null)
                WriteHashIndex(updated.RequestHash, updated.JudgmentId, JudgmentStatuses.Failed);
            return updated;
        }
    }

    public JudgmentRecord MarkDeferred(string judgmentId)
    {
        lock (_gate)
        {
            var record = LoadRequired(judgmentId);
            if (record.Status is JudgmentStatuses.Completed or JudgmentStatuses.Failed)
                return record;
            var updated = record with { Status = JudgmentStatuses.Deferred };
            SaveRecord(updated);
            return updated;
        }
    }

    public JudgmentRecord? TryGet(string judgmentId)
    {
        lock (_gate)
        {
            var text = AtomicFile.ReadAllTextIfExists(RecordPath(judgmentId));
            return text is null ? null : JudgmentJson.Deserialize<JudgmentRecord>(text);
        }
    }

    public JudgmentSuccess? TryLoadSuccess(string judgmentId)
    {
        var record = TryGet(judgmentId);
        if (record is null || record.Status != JudgmentStatuses.Completed || record.ResponseObjectId is null)
            return null;
        var text = _objects.TryReadTextById(record.ResponseObjectId);
        return text is null ? null : JudgmentJson.Deserialize<JudgmentSuccess>(text);
    }

    public JudgmentRecord? TryGetCompletedByRequestHash(string requestHash)
    {
        lock (_gate)
            return TryLoadCompletedByHash(requestHash);
    }

    /// <summary>Restart recovery: requested judgments without a completed/failed terminal response.</summary>
    public IReadOnlyList<JudgmentRecord> ListUnresolved()
    {
        lock (_gate)
        {
            if (!Directory.Exists(JudgmentsDirectory)) return [];
            var list = new List<JudgmentRecord>();
            foreach (var path in Directory.EnumerateFiles(JudgmentsDirectory, "*.json"))
            {
                var text = AtomicFile.ReadAllTextIfExists(path);
                if (text is null) continue;
                var record = JudgmentJson.Deserialize<JudgmentRecord>(text);
                if (record.Status is JudgmentStatuses.Requested or JudgmentStatuses.Deferred)
                    list.Add(record);
            }
            return list.OrderBy(r => r.CreatedAt).ToList();
        }
    }

    public IReadOnlyList<JudgmentRecord> ListAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(JudgmentsDirectory)) return [];
            var list = new List<JudgmentRecord>();
            foreach (var path in Directory.EnumerateFiles(JudgmentsDirectory, "*.json"))
            {
                var text = AtomicFile.ReadAllTextIfExists(path);
                if (text is null) continue;
                list.Add(JudgmentJson.Deserialize<JudgmentRecord>(text));
            }
            return list.OrderBy(r => r.CreatedAt).ToList();
        }
    }

    /// <summary>Metadata-only payload safe for case events, ledger, and diagnostics.</summary>
    public static object ToAuditPayload(JudgmentRecord record) => new
    {
        judgmentId = record.JudgmentId,
        provider = record.Provider,
        requestObjectId = record.RequestObjectId,
        requestHash = record.RequestHash,
        responseObjectId = record.ResponseObjectId,
        responseHash = record.ResponseHash,
        questionSetId = record.QuestionSetId,
        questionSetVersion = record.QuestionSetVersion,
        model = record.Model,
        status = record.Status,
        failureCategory = record.FailureCategory,
        inputTokens = record.InputTokens,
        outputTokens = record.OutputTokens,
        elapsedMs = record.ElapsedMs,
    };

    private JudgmentRecord? TryLoadCompletedByHash(string requestHash)
    {
        var indexText = AtomicFile.ReadAllTextIfExists(HashIndexPath(requestHash));
        if (indexText is null) return null;
        using var doc = JsonDocument.Parse(indexText);
        if (!doc.RootElement.TryGetProperty("judgmentId", out var idEl)) return null;
        if (!doc.RootElement.TryGetProperty("status", out var statusEl)) return null;
        if (!string.Equals(statusEl.GetString(), JudgmentStatuses.Completed, StringComparison.Ordinal))
            return null;
        var id = idEl.GetString();
        if (id is null) return null;
        var record = TryGetUnlocked(id);
        return record is { Status: JudgmentStatuses.Completed } ? record : null;
    }

    private JudgmentRecord? TryGetUnlocked(string judgmentId)
    {
        var text = AtomicFile.ReadAllTextIfExists(RecordPath(judgmentId));
        return text is null ? null : JudgmentJson.Deserialize<JudgmentRecord>(text);
    }

    private JudgmentRecord LoadRequired(string judgmentId) =>
        TryGetUnlocked(judgmentId) ?? throw new InvalidOperationException($"Unknown judgment '{judgmentId}'.");

    private void SaveRecord(JudgmentRecord record)
    {
        Directory.CreateDirectory(JudgmentsDirectory);
        AtomicFile.WriteAllText(RecordPath(record.JudgmentId), JudgmentJson.Serialize(record));
    }

    private void WriteHashIndex(string requestHash, string judgmentId, string status)
    {
        var dir = Path.Combine(JudgmentsDirectory, "by-hash");
        Directory.CreateDirectory(dir);
        var payload = JudgmentJson.Serialize(new { judgmentId, status, requestHash });
        AtomicFile.WriteAllText(HashIndexPath(requestHash), payload);
    }
}

public sealed record JudgmentDispatchHandle(
    JudgmentRecord Record,
    bool AlreadyComplete,
    bool Dispatched);

/// <summary>Lookup facade over completed judgments keyed by request hash.</summary>
public sealed class JudgmentCache
{
    private readonly JudgmentStore _store;

    public JudgmentCache(JudgmentStore store) => _store = store;

    public JudgmentRecord? TryGetCompleted(string requestHash) =>
        _store.TryGetCompletedByRequestHash(requestHash);

    public JudgmentSuccess? TryGetCompletedSuccess(string requestHash)
    {
        var record = TryGetCompleted(requestHash);
        return record is null ? null : _store.TryLoadSuccess(record.JudgmentId);
    }
}
