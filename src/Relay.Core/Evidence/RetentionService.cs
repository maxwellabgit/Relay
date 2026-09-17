using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Relay.Core.Cases;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Evidence;

public sealed class RetentionAuditEntry
{
    [JsonPropertyName("auditId")] public required string AuditId { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
    [JsonPropertyName("objectIds")] public List<string> ObjectIds { get; init; } = [];
    [JsonPropertyName("contentHashes")] public List<string> ContentHashes { get; init; } = [];
    [JsonPropertyName("notes")] public List<string> Notes { get; init; } = [];
    // Intentionally no body/text fields — audit has ids/hashes only.
}

/// <summary>
/// Transcript retention: 30-day default, per-session override, immediate deletion,
/// separately retained excerpts, content-addressed blob ownership.
/// </summary>
public sealed class RetentionService
{
    public static readonly TimeSpan DefaultTranscriptTtl = TimeSpan.FromDays(30);

    private readonly DataRoot _root;
    private readonly EvidenceStore _evidence;
    private readonly ObjectStore _objects;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public RetentionService(DataRoot root, EvidenceStore evidence, ObjectStore objects, IClock clock)
    {
        _root = root;
        _evidence = evidence;
        _objects = objects;
        _clock = clock;
    }

    public string AuditDirectory => Path.Combine(_root.Path, "retention", "audit");
    public string SessionPolicyDirectory => Path.Combine(_root.Path, "retention", "sessions");

    public void SetSessionPolicy(string sessionId, TimeSpan? ttlOverride, bool deleteExcerptsWithSession = false)
    {
        Directory.CreateDirectory(SessionPolicyDirectory);
        var policy = new
        {
            sessionId,
            ttlDays = (ttlOverride ?? DefaultTranscriptTtl).TotalDays,
            deleteExcerptsWithSession,
            updatedAt = _clock.UtcNow,
        };
        AtomicFile.WriteAllText(
            Path.Combine(SessionPolicyDirectory, sessionId + ".json"),
            JsonSerializer.Serialize(policy, RelayJson.Indented));
    }

    public (TimeSpan Ttl, bool DeleteExcerptsWithSession) GetSessionPolicy(string sessionId)
    {
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(SessionPolicyDirectory, sessionId + ".json"));
        if (text is null) return (DefaultTranscriptTtl, false);
        using var doc = JsonDocument.Parse(text);
        var ttlDays = doc.RootElement.TryGetProperty("ttlDays", out var d) ? d.GetDouble() : DefaultTranscriptTtl.TotalDays;
        var del = doc.RootElement.TryGetProperty("deleteExcerptsWithSession", out var e) && e.GetBoolean();
        return (TimeSpan.FromDays(ttlDays), del);
    }

    /// <summary>Retain an excerpt separately from the session transcript.</summary>
    public string RetainExcerpt(string sessionId, string text, string sourceArtifactId, string contentHash)
    {
        Directory.CreateDirectory(_root.ExcerptsDirectory);
        var id = Ulid.NewUlid(_clock.UtcNow);
        var blob = new
        {
            excerptId = id,
            sessionId,
            sourceArtifactId,
            contentHash,
            text,
            retainedAt = _clock.UtcNow,
        };
        AtomicFile.WriteAllText(
            Path.Combine(_root.ExcerptsDirectory, id + ".json"),
            JsonSerializer.Serialize(blob, RelayJson.Indented));
        return id;
    }

    public RetentionAuditEntry DeleteSession(
        string sessionId,
        bool immediate = true,
        bool? deleteExcerpts = null,
        IEnumerable<string>? survivingSummaryArtifactIds = null)
    {
        var (_, policyDeleteExcerpts) = GetSessionPolicy(sessionId);
        var removeExcerpts = deleteExcerpts ?? policyDeleteExcerpts;

        var objectIds = new List<string>();
        var hashes = new List<string>();

        // Evidence artifacts for session
        foreach (var artifact in _evidence.ListAll().Where(a => a.SessionIds.Contains(sessionId)))
        {
            objectIds.Add(artifact.ArtifactId);
            hashes.Add(artifact.ContentHash);
            TryDeleteEvidenceArtifact(artifact.ArtifactId, artifact.ContentHash, hashes);
        }

        // Object-store transcript segments / caches / payloads keyed by session refs in evidence already covered;
        // also clear feed text projections for the session when case id == session id.
        TryClearFeedText(sessionId);
        TryClearDevPayloads(sessionId);
        TryClearIndexes(sessionId);

        if (removeExcerpts)
            DeleteExcerptsForSession(sessionId, objectIds, hashes);

        foreach (var summaryId in survivingSummaryArtifactIds ?? [])
            MarkOriginalEvidenceUnavailable(summaryId);

        var audit = new RetentionAuditEntry
        {
            AuditId = Ulid.NewUlid(_clock.UtcNow),
            SessionId = sessionId,
            Action = immediate ? "immediate_delete" : "expiry_delete",
            At = _clock.UtcNow,
            ObjectIds = objectIds.Distinct(StringComparer.Ordinal).ToList(),
            ContentHashes = hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Notes = [removeExcerpts ? "excerpts_deleted" : "excerpts_retained"],
        };
        PersistAudit(audit);
        return audit;
    }

    public IReadOnlyList<RetentionAuditEntry> ExpireDue(DateTimeOffset now)
    {
        var audits = new List<RetentionAuditEntry>();
        foreach (var artifact in _evidence.ListAll())
        {
            if (artifact.Kind != "transcript") continue;
            foreach (var sessionId in artifact.SessionIds)
            {
                var (ttl, _) = GetSessionPolicy(sessionId);
                var expires = artifact.ExpiresAt ?? artifact.CreatedAt + ttl;
                if (expires <= now)
                    audits.Add(DeleteSession(sessionId, immediate: false));
            }
        }
        return audits;
    }

    public void MarkOriginalEvidenceUnavailable(string summaryArtifactId)
    {
        var notePath = Path.Combine(_root.EvidenceDirectory, "unavailable", summaryArtifactId + ".flag");
        Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
        AtomicFile.WriteAllText(notePath, JsonSerializer.Serialize(new
        {
            artifactId = summaryArtifactId,
            original_evidence_unavailable = true,
            at = _clock.UtcNow,
        }, RelayJson.Indented));

        var path = _evidence.ArtifactPath(summaryArtifactId);
        var text = AtomicFile.ReadAllTextIfExists(path);
        if (text is null) return;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
            dict["original_evidence_unavailable"] = JsonSerializer.SerializeToElement(true);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(dict, RelayJson.Indented));
        }
        catch (JsonException)
        {
            // Flag file is authoritative.
        }
    }

    public bool IsOriginalEvidenceUnavailable(string summaryArtifactId)
        => File.Exists(Path.Combine(_root.EvidenceDirectory, "unavailable", summaryArtifactId + ".flag"));

    private void TryDeleteEvidenceArtifact(string artifactId, string contentHash, List<string> hashes)
    {
        lock (_gate)
        {
            var path = _evidence.ArtifactPath(artifactId);
            if (File.Exists(path)) File.Delete(path);

            // Content-addressed blob: delete only when no other artifact owns the hash.
            var stillOwned = _evidence.ListAll().Any(a =>
                a.ArtifactId != artifactId
                && string.Equals(a.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase));
            if (!stillOwned)
            {
                var blob = Path.Combine(_root.EvidenceDirectory, "blobs", contentHash + ".bin");
                if (File.Exists(blob)) File.Delete(blob);
                hashes.Add(contentHash);
            }
        }
    }

    private void DeleteExcerptsForSession(string sessionId, List<string> objectIds, List<string> hashes)
    {
        if (!Directory.Exists(_root.ExcerptsDirectory)) return;
        foreach (var file in Directory.GetFiles(_root.ExcerptsDirectory, "*.json"))
        {
            var text = AtomicFile.ReadAllTextIfExists(file);
            if (text is null) continue;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("sessionId", out var s)
                && s.GetString() == sessionId)
            {
                if (doc.RootElement.TryGetProperty("excerptId", out var id))
                    objectIds.Add(id.GetString() ?? Path.GetFileNameWithoutExtension(file));
                if (doc.RootElement.TryGetProperty("contentHash", out var h) && h.GetString() is { } hash)
                    hashes.Add(hash);
                File.Delete(file);
            }
        }
    }

    private void TryClearFeedText(string sessionId)
    {
        // Projection DB may hold feed rows; best-effort delete via SQLite if present.
        var db = _root.ProjectionsDatabasePath;
        if (!File.Exists(db)) return;
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM feed_items WHERE case_id = $id;";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Projections schema may differ; deletion still covers evidence/objects.
        }
    }

    private void TryClearDevPayloads(string sessionId)
    {
        var runs = _root.DevRunsDirectory;
        if (!Directory.Exists(runs)) return;
        // Do not wipe whole runs; strip session-tagged payload files if present.
        foreach (var file in Directory.EnumerateFiles(runs, "*" + sessionId + "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    private void TryClearIndexes(string sessionId)
    {
        var idx = Path.Combine(_root.Path, "indexes", "sessions", sessionId + ".json");
        if (File.Exists(idx)) File.Delete(idx);
    }

    private void PersistAudit(RetentionAuditEntry audit)
    {
        Directory.CreateDirectory(AuditDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(AuditDirectory, audit.AuditId + ".json"),
            JsonSerializer.Serialize(audit, RelayJson.Indented));
    }
}
