using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Evidence;

/// <summary>
/// Persists content artifacts with provenance metadata. Authoritative files under
/// <c>evidence/artifacts/{artifactId}.json</c>.
/// </summary>
public sealed class EvidenceStore
{
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public EvidenceStore(DataRoot root, IClock clock)
    {
        _root = root;
        _clock = clock;
    }

    public string ArtifactsDirectory => Path.Combine(_root.EvidenceDirectory, "artifacts");
    public string StatementsDirectory => Path.Combine(_root.EvidenceDirectory, "statements");
    public string ConflictsDirectory => Path.Combine(_root.EvidenceDirectory, "conflicts");

    public string ArtifactPath(string artifactId) => Path.Combine(ArtifactsDirectory, artifactId + ".json");

    public ContentArtifact Put(
        ReadOnlySpan<byte> content,
        IEnumerable<string>? sourceRefs = null,
        IEnumerable<string>? projectIds = null,
        IEnumerable<string>? sessionIds = null,
        string restriction = ContentRestriction.LocalOnly,
        IEnumerable<string>? inputArtifactIds = null,
        string? label = null,
        string? artifactId = null,
        DateTimeOffset? expiresAt = null,
        string kind = "content")
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var id = artifactId ?? Ulid.NewUlid(_clock.UtcNow);
        var inputs = inputArtifactIds?.ToList() ?? [];

        // Derived artifacts: export restriction ≥ inputs (local_only wins).
        var effective = restriction;
        if (inputs.Count > 0)
        {
            var inputRestrictions = inputs
                .Select(TryLoad)
                .Where(a => a is not null)
                .Select(a => a!.Restriction);
            effective = ContentRestriction.Max(
                new[] { restriction }.Concat(inputRestrictions));
        }

        var artifact = new ContentArtifact
        {
            ArtifactId = id,
            ContentHash = hash,
            Kind = kind,
            SourceRefs = sourceRefs?.ToList() ?? [],
            ProjectIds = projectIds?.Distinct(StringComparer.Ordinal).ToList() ?? [],
            SessionIds = sessionIds?.Distinct(StringComparer.Ordinal).ToList() ?? [],
            Restriction = effective,
            CreatedAt = _clock.UtcNow,
            ExpiresAt = expiresAt,
            InputArtifactIds = inputs,
            Label = label,
        };

        lock (_gate)
        {
            Directory.CreateDirectory(ArtifactsDirectory);
            AtomicFile.WriteAllText(ArtifactPath(id), JsonSerializer.Serialize(artifact, RelayJson.Indented));
            var blobDir = Path.Combine(_root.EvidenceDirectory, "blobs");
            Directory.CreateDirectory(blobDir);
            var blobPath = Path.Combine(blobDir, hash + ".bin");
            if (!File.Exists(blobPath))
                AtomicFile.WriteAllBytes(blobPath, content.ToArray());
        }

        return artifact;
    }

    public ContentArtifact PutText(
        string text,
        IEnumerable<string>? sourceRefs = null,
        IEnumerable<string>? projectIds = null,
        IEnumerable<string>? sessionIds = null,
        string restriction = ContentRestriction.LocalOnly,
        IEnumerable<string>? inputArtifactIds = null,
        string? label = null,
        string? artifactId = null,
        DateTimeOffset? expiresAt = null,
        string kind = "content")
        => Put(Encoding.UTF8.GetBytes(text), sourceRefs, projectIds, sessionIds, restriction,
            inputArtifactIds, label, artifactId, expiresAt, kind);

    /// <summary>
    /// Records a derived artifact (summary, query, decision question). Restriction inherits
    /// from inputs — never weaker than any input.
    /// </summary>
    public ContentArtifact Derive(
        string text,
        IReadOnlyList<string> inputArtifactIds,
        string? label = null,
        string kind = "derived",
        IEnumerable<string>? extraSourceRefs = null)
    {
        var inputs = inputArtifactIds.Select(TryLoad).Where(a => a is not null).Cast<ContentArtifact>().ToList();
        var restriction = ContentRestriction.Max(inputs.Select(a => a.Restriction).DefaultIfEmpty(ContentRestriction.LocalOnly));
        var projects = inputs.SelectMany(a => a.ProjectIds).Distinct(StringComparer.Ordinal).ToList();
        var sessions = inputs.SelectMany(a => a.SessionIds).Distinct(StringComparer.Ordinal).ToList();
        var sources = inputs.SelectMany(a => a.SourceRefs)
            .Concat(extraSourceRefs ?? [])
            .Concat(inputArtifactIds.Select(id => "artifact:" + id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return PutText(text, sources, projects, sessions, restriction, inputArtifactIds, label, kind: kind);
    }

    public ContentArtifact? TryLoad(string artifactId)
    {
        var text = AtomicFile.ReadAllTextIfExists(ArtifactPath(artifactId));
        return text is null ? null : JsonSerializer.Deserialize<ContentArtifact>(text, RelayJson.Indented);
    }

    public byte[]? TryReadBlob(string contentHash)
    {
        var path = Path.Combine(_root.EvidenceDirectory, "blobs", contentHash + ".bin");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public string? TryReadText(string artifactId)
    {
        var artifact = TryLoad(artifactId);
        if (artifact is null) return null;
        var bytes = TryReadBlob(artifact.ContentHash);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public IReadOnlyList<ContentArtifact> ListAll()
    {
        if (!Directory.Exists(ArtifactsDirectory)) return [];
        var list = new List<ContentArtifact>();
        foreach (var file in Directory.GetFiles(ArtifactsDirectory, "*.json"))
        {
            var text = AtomicFile.ReadAllTextIfExists(file);
            if (text is null) continue;
            var a = JsonSerializer.Deserialize<ContentArtifact>(text, RelayJson.Indented);
            if (a is not null) list.Add(a);
        }
        return list;
    }

    public EvidenceStatement SaveStatement(EvidenceStatement statement)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(StatementsDirectory);
            AtomicFile.WriteAllText(
                Path.Combine(StatementsDirectory, statement.StatementId + ".json"),
                JsonSerializer.Serialize(statement, RelayJson.Indented));
            return statement;
        }
    }

    public EvidenceConflict SaveConflict(EvidenceConflict conflict)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(ConflictsDirectory);
            AtomicFile.WriteAllText(
                Path.Combine(ConflictsDirectory, conflict.ConflictId + ".json"),
                JsonSerializer.Serialize(conflict, RelayJson.Indented));
            return conflict;
        }
    }
}
