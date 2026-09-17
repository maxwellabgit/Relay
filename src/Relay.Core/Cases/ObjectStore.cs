using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>
/// Content-addressed object store. Large or sensitive content lives here; the ledger and case events
/// only keep IDs and hashes. Never put raw overheard conversation into ledger data — store it here
/// and reference the hash.
/// </summary>
public sealed class ObjectStore
{
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public ObjectStore(DataRoot root, IClock clock)
    {
        _root = root;
        _clock = clock;
    }

    public string ObjectsDirectory => _root.ObjectsDirectory;

    /// <summary>Stores UTF-8 JSON (or any text) content-addressed by SHA-256. Returns objectId + hash.</summary>
    public StoredObject PutJson(object value, string? objectId = null)
    {
        var json = JsonSerializer.Serialize(value, RelayJson.Compact);
        return PutBytes(Encoding.UTF8.GetBytes(json), objectId, contentType: "application/json");
    }

    public StoredObject PutText(string text, string? objectId = null, string contentType = "text/plain")
        => PutBytes(Encoding.UTF8.GetBytes(text), objectId, contentType);

    public StoredObject PutBytes(ReadOnlySpan<byte> bytes, string? objectId = null, string contentType = "application/octet-stream")
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var id = objectId ?? Ulid.NewUlid(_clock.UtcNow);
        var path = PathForHash(hash);

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                AtomicFile.WriteAllBytes(path, bytes);
            }

            // Sidecar id → hash so callers can look up by objectId.
            var metaPath = Path.Combine(_root.ObjectsDirectory, "by-id", id + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            var meta = new { objectId = id, sha256 = hash, contentType, storedAt = _clock.UtcNow };
            AtomicFile.WriteAllText(metaPath, JsonSerializer.Serialize(meta, RelayJson.Indented));
        }

        return new StoredObject(id, hash, path);
    }

    public string? TryReadTextByHash(string sha256)
    {
        var path = PathForHash(sha256);
        return AtomicFile.ReadAllTextIfExists(path);
    }

    public string PathForHash(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length < 4)
            throw new ArgumentException("SHA-256 hex expected.", nameof(sha256));
        var prefix = sha256[..2].ToLowerInvariant();
        return Path.Combine(_root.ObjectsDirectory, "sha256", prefix, sha256.ToLowerInvariant() + ".json");
    }
}
