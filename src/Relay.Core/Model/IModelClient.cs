namespace Relay.Core.Model;

public sealed record ModelMessage(string Role, string Content);

/// <summary>
/// One chat completion request. The gateway sends exactly this and nothing else. When
/// <see cref="JsonSchema"/> is set the gateway asks for schema-constrained output (llama.cpp turns
/// it into a grammar; OpenAI-compatible hosts use structured outputs); <see cref="JsonObject"/> is
/// the weaker "any JSON object" request for hosts that cannot take a schema.
/// </summary>
public sealed record ModelRequest(string Model, IReadOnlyList<ModelMessage> Messages, int MaxOutputTokens, bool JsonObject, string? JsonSchema = null, string SchemaName = "relay");

public sealed record ModelResponse(bool Ok, string? Content, int PromptTokens, int CompletionTokens, long ElapsedMs, string? Error, int? HttpStatus = null)
{
    public static ModelResponse Failed(string error, long elapsedMs, int? status = null) => new(false, null, 0, 0, elapsedMs, error, status);
}

/// <summary>
/// The only way a model is reached. Implementations live outside Relay.Core (the core has no
/// network); the implementation talks to one allow-listed OpenAI-compatible endpoint: loopback
/// http for the local RELAY0 model, https for anything else.
/// </summary>
public interface IModelClient
{
    string Host { get; }
    string Model { get; }
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The same completion, streamed: <paramref name="onDelta"/> receives each piece of content as it arrives
    /// (server-sent events on an OpenAI-compatible host), and the assembled reply is returned at the end
    /// exactly as <see cref="CompleteAsync"/> would have. Clients without a streaming transport complete
    /// normally and deliver the whole content as one delta, so callers never depend on the transport.
    /// </summary>
    async Task<ModelResponse> StreamAsync(ModelRequest request, Func<string, CancellationToken, Task> onDelta, CancellationToken cancellationToken)
    {
        var response = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Ok && !string.IsNullOrEmpty(response.Content)) await onDelta(response.Content, cancellationToken).ConfigureAwait(false);
        return response;
    }
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
