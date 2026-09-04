namespace Relay.Core.Model;

public sealed record ModelMessage(string Role, string Content);

/// <summary>One chat completion request. The gateway sends exactly this and nothing else.</summary>
public sealed record ModelRequest(string Model, IReadOnlyList<ModelMessage> Messages, int MaxOutputTokens, bool JsonObject);

public sealed record ModelResponse(bool Ok, string? Content, int PromptTokens, int CompletionTokens, long ElapsedMs, string? Error, int? HttpStatus = null)
{
    public static ModelResponse Failed(string error, long elapsedMs, int? status = null) => new(false, null, 0, 0, elapsedMs, error, status);
}

/// <summary>
/// The only way a model is reached. Implementations live outside Relay.Core (the core has no
/// network); the single implementation talks to one allow-listed OpenAI-compatible endpoint.
/// </summary>
public interface IModelClient
{
    string Host { get; }
    string Model { get; }
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>Named secrets at rest (API keys). Values never appear in settings, the ledger, or incidents.</summary>
public interface ISecretStore
{
    bool Exists(string name);
    string? Get(string name);
    void Set(string name, string value);
    bool Remove(string name);
}

/// <summary>In-memory store for tests and for hosts without a protected store; nothing is persisted.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public bool Exists(string name) => _values.ContainsKey(name);
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public void Set(string name, string value) => _values[name] = value;
    public bool Remove(string name) => _values.Remove(name);
}
