namespace Relay.Core.Judgments;

/// <summary>Judgment transport client. Implementations must never log or persist API keys.</summary>
public interface IJudgmentClient
{
    string Name { get; }
    Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Reads secrets from process environment. DPAPI-backed store is a later Windows concern.</summary>
public sealed class EnvironmentSecretProvider : Model.ISecretStore
{
    public bool Exists(string name)
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    public string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public void Set(string name, string value)
        => Environment.SetEnvironmentVariable(name, value);

    public bool Remove(string name)
    {
        var had = Exists(name);
        Environment.SetEnvironmentVariable(name, null);
        return had;
    }
}

/// <summary>Live client preflight: requires a non-empty API key secret.</summary>
public static class JudgmentLivePreflight
{
    public static void EnsureApiKey(Model.ISecretStore secrets, string secretName = JudgmentDefaults.ApiKeySecretName)
    {
        var key = secrets.Get(secretName);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"Live Jev transport preflight failed: secret '{secretName}' is missing. " +
                $"Set env {JudgmentDefaults.ApiKeyEnvVar} (never commit keys).");
    }
}
