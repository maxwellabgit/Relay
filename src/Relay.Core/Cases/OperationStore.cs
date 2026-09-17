using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Cases;

/// <summary>
/// Persists operation envelopes under <c>operations/{operationId}.json</c> and indexes by
/// idempotency key so a duplicate completion is a no-op.
/// </summary>
public sealed class OperationStore
{
    private readonly DataRoot _root;
    private readonly object _gate = new();

    public OperationStore(DataRoot root) => _root = root;

    public string OperationPath(string operationId) => Path.Combine(_root.OperationsDirectory, operationId + ".json");
    public string IdempotencyIndexPath(string idempotencyKey)
    {
        var safe = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idempotencyKey)));
        return Path.Combine(_root.OperationsDirectory, "by-idempotency", safe + ".json");
    }

    public void Save(OperationEnvelope envelope)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root.OperationsDirectory);
            AtomicFile.WriteAllText(OperationPath(envelope.OperationId), JsonSerializer.Serialize(envelope, RelayJson.Indented));

            Directory.CreateDirectory(Path.GetDirectoryName(IdempotencyIndexPath(envelope.IdempotencyKey))!);
            var index = new { idempotencyKey = envelope.IdempotencyKey, operationId = envelope.OperationId, status = envelope.Status };
            AtomicFile.WriteAllText(IdempotencyIndexPath(envelope.IdempotencyKey), JsonSerializer.Serialize(index, RelayJson.Indented));
        }
    }

    public OperationEnvelope? TryLoad(string operationId)
    {
        var text = AtomicFile.ReadAllTextIfExists(OperationPath(operationId));
        return text is null ? null : JsonSerializer.Deserialize<OperationEnvelope>(text, RelayJson.Indented);
    }

    /// <summary>Returns the operation currently indexed under this idempotency key, if any.</summary>
    public OperationEnvelope? TryFindByIdempotencyKey(string idempotencyKey)
    {
        var text = AtomicFile.ReadAllTextIfExists(IdempotencyIndexPath(idempotencyKey));
        if (text is null) return null;
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("operationId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        return TryLoad(idEl.GetString()!);
    }

    public IReadOnlyList<OperationEnvelope> ListAll()
    {
        if (!Directory.Exists(_root.OperationsDirectory)) return [];
        var list = new List<OperationEnvelope>();
        foreach (var file in Directory.GetFiles(_root.OperationsDirectory, "*.json"))
        {
            var text = AtomicFile.ReadAllTextIfExists(file);
            if (text is null) continue;
            var env = JsonSerializer.Deserialize<OperationEnvelope>(text, RelayJson.Indented);
            if (env is not null) list.Add(env);
        }
        return list;
    }
}
