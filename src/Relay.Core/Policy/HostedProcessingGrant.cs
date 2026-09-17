using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Policy;

public static class HostedPurposes
{
    public const string Judgment = "judgment";
    public const string Reasoning = "reasoning";
    public const string Search = "search";
    public const string DecisionQuestion = "decision_question";

    public static readonly string[] All = [Judgment, Reasoning, Search, DecisionQuestion];
}

/// <summary>
/// Standing grant for hosted processing (Jev / hosted reasoning / hosted search).
/// Scope and limits are authoritative; revocationVersion increments on revoke.
/// </summary>
public sealed class HostedProcessingGrant
{
    [JsonPropertyName("grantId")] public required string GrantId { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("projectIds")] public List<string> ProjectIds { get; set; } = [];
    [JsonPropertyName("sessionIds")] public List<string> SessionIds { get; set; } = [];
    /// <summary>Allowed source artifact/ref prefixes or ids.</summary>
    [JsonPropertyName("allowedSourceScope")] public List<string> AllowedSourceScope { get; set; } = [];
    [JsonPropertyName("allowedPurposes")] public List<string> AllowedPurposes { get; set; } = [];
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("maxRequests")] public int MaxRequests { get; set; } = int.MaxValue;
    [JsonPropertyName("maxInputTokens")] public long MaxInputTokens { get; set; } = long.MaxValue;
    [JsonPropertyName("maxSpendingMicros")] public long MaxSpendingMicros { get; set; } = long.MaxValue;
    [JsonPropertyName("requestsUsed")] public int RequestsUsed { get; set; }
    [JsonPropertyName("inputTokensUsed")] public long InputTokensUsed { get; set; }
    [JsonPropertyName("spendingMicrosUsed")] public long SpendingMicrosUsed { get; set; }
    [JsonPropertyName("revocationVersion")] public long RevocationVersion { get; set; }
    [JsonPropertyName("revoked")] public bool Revoked { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("pricingAssumptionVersion")] public string PricingAssumptionVersion { get; set; } = "pricing-v1";
}

/// <summary>Durable store for hosted processing grants.</summary>
public sealed class HostedGrantStore
{
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public HostedGrantStore(DataRoot root, IClock clock)
    {
        _root = root;
        _clock = clock;
    }

    public string GrantsDirectory => Path.Combine(_root.GrantsDirectory);
    public string GrantPath(string grantId) => Path.Combine(GrantsDirectory, grantId + ".json");

    public HostedProcessingGrant Create(
        string provider,
        IEnumerable<string>? projectIds = null,
        IEnumerable<string>? sessionIds = null,
        IEnumerable<string>? allowedSourceScope = null,
        IEnumerable<string>? allowedPurposes = null,
        DateTimeOffset? expiresAt = null,
        int maxRequests = int.MaxValue,
        long maxInputTokens = long.MaxValue,
        long maxSpendingMicros = long.MaxValue,
        string pricingAssumptionVersion = "pricing-v1")
    {
        var grant = new HostedProcessingGrant
        {
            GrantId = Ulid.NewUlid(_clock.UtcNow),
            Provider = provider,
            ProjectIds = projectIds?.ToList() ?? [],
            SessionIds = sessionIds?.ToList() ?? [],
            AllowedSourceScope = allowedSourceScope?.ToList() ?? ["*"],
            AllowedPurposes = allowedPurposes?.ToList() ?? HostedPurposes.All.ToList(),
            ExpiresAt = expiresAt ?? _clock.UtcNow.AddDays(30),
            MaxRequests = maxRequests,
            MaxInputTokens = maxInputTokens,
            MaxSpendingMicros = maxSpendingMicros,
            CreatedAt = _clock.UtcNow,
            PricingAssumptionVersion = pricingAssumptionVersion,
        };
        Save(grant);
        return grant;
    }

    public void Save(HostedProcessingGrant grant)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(GrantsDirectory);
            AtomicFile.WriteAllText(GrantPath(grant.GrantId), JsonSerializer.Serialize(grant, RelayJson.Indented));
        }
    }

    public HostedProcessingGrant? TryLoad(string grantId)
    {
        var text = AtomicFile.ReadAllTextIfExists(GrantPath(grantId));
        return text is null ? null : JsonSerializer.Deserialize<HostedProcessingGrant>(text, RelayJson.Indented);
    }

    public HostedProcessingGrant Revoke(string grantId)
    {
        lock (_gate)
        {
            var grant = TryLoad(grantId) ?? throw new InvalidOperationException($"Grant '{grantId}' not found.");
            grant.Revoked = true;
            grant.RevocationVersion++;
            Save(grant);
            return grant;
        }
    }

    public IReadOnlyList<HostedProcessingGrant> ListAll()
    {
        if (!Directory.Exists(GrantsDirectory)) return [];
        var list = new List<HostedProcessingGrant>();
        foreach (var file in Directory.GetFiles(GrantsDirectory, "*.json"))
        {
            var text = AtomicFile.ReadAllTextIfExists(file);
            if (text is null) continue;
            var g = JsonSerializer.Deserialize<HostedProcessingGrant>(text, RelayJson.Indented);
            if (g is not null) list.Add(g);
        }
        return list;
    }

    /// <summary>Active (non-revoked, non-expired) grants for a provider.</summary>
    public IReadOnlyList<HostedProcessingGrant> ListActive(string? provider = null)
    {
        var now = _clock.UtcNow;
        return ListAll()
            .Where(g => !g.Revoked && g.ExpiresAt > now)
            .Where(g => provider is null || string.Equals(g.Provider, provider, StringComparison.Ordinal))
            .ToList();
    }
}
