using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Privacy;

/// <summary>Durable hosted-processing grants and append-only grant audit events (IDs/hashes only).</summary>
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

    public string GrantsDirectory => Path.Combine(_root.Path, "grants", "hosted");
    private string GrantPath(string grantId) => Path.Combine(GrantsDirectory, grantId + ".json");
    private string EventsPath => Path.Combine(GrantsDirectory, "events.jsonl");

    public HostedProcessingGrant CreateSessionGrant(
        string sessionId,
        IEnumerable<string> purposes,
        IEnumerable<string> allowedClassifications,
        int maximumInputTokenBudget,
        DateTimeOffset? expiresAt = null,
        string? grantId = null)
    {
        var grant = new HostedProcessingGrant
        {
            GrantId = grantId ?? Ulid.NewUlid(_clock.UtcNow),
            Scope = HostedGrantScopes.Session,
            SessionId = sessionId,
            AllowedProvider = HostedProviders.TypeSafe,
            AllowedSourceClassifications = allowedClassifications.Distinct(StringComparer.Ordinal).ToList(),
            AllowedPurposes = purposes.Distinct(StringComparer.Ordinal).ToList(),
            MaximumInputTokenBudget = maximumInputTokenBudget,
            TokensUsed = 0,
            CreatedAt = _clock.UtcNow,
            ExpiresAt = expiresAt,
        };
        grant.ValidateShape();
        Save(grant);
        AppendEvent(HostedGrantEventTypes.Created, grant);
        return grant;
    }

    public HostedProcessingGrant CreateProjectGrant(
        string projectId,
        IEnumerable<string> purposes,
        IEnumerable<string> allowedClassifications,
        int maximumInputTokenBudget,
        DateTimeOffset? expiresAt = null,
        string? grantId = null)
    {
        var classifications = allowedClassifications.Distinct(StringComparer.Ordinal).ToList();
        if (classifications.Contains(SourceClassification.HostedAllowedSession, StringComparer.Ordinal))
        {
            throw new DisclosureException(
                "Project grants cannot authorize hosted_allowed_session classifications.",
                "validation");
        }

        var grant = new HostedProcessingGrant
        {
            GrantId = grantId ?? Ulid.NewUlid(_clock.UtcNow),
            Scope = HostedGrantScopes.Project,
            ProjectId = projectId,
            AllowedProvider = HostedProviders.TypeSafe,
            AllowedSourceClassifications = classifications,
            AllowedPurposes = purposes.Distinct(StringComparer.Ordinal).ToList(),
            MaximumInputTokenBudget = maximumInputTokenBudget,
            TokensUsed = 0,
            CreatedAt = _clock.UtcNow,
            ExpiresAt = expiresAt,
        };
        grant.ValidateShape();
        Save(grant);
        AppendEvent(HostedGrantEventTypes.Created, grant);
        return grant;
    }

    public HostedProcessingGrant? TryGet(string grantId)
    {
        lock (_gate)
        {
            var text = AtomicFile.ReadAllTextIfExists(GrantPath(grantId));
            return text is null ? null : JsonSerializer.Deserialize<HostedProcessingGrant>(text, RelayJson.Indented);
        }
    }

    public HostedProcessingGrant Revoke(string grantId)
    {
        lock (_gate)
        {
            var grant = TryGetUnlocked(grantId)
                ?? throw new DisclosureException($"Unknown grant '{grantId}'.");
            if (grant.IsRevoked) return grant;
            grant.RevokedAt = _clock.UtcNow;
            SaveUnlocked(grant);
            AppendEventUnlocked(HostedGrantEventTypes.Revoked, grant);
            return grant;
        }
    }

    public HostedProcessingGrant RecordTokenUse(string grantId, int inputTokens)
    {
        if (inputTokens < 0)
            throw new DisclosureException("inputTokens must be non-negative.", "validation");
        lock (_gate)
        {
            var grant = TryGetUnlocked(grantId)
                ?? throw new DisclosureException($"Unknown grant '{grantId}'.");
            grant.TokensUsed += inputTokens;
            SaveUnlocked(grant);
            AppendEventUnlocked(HostedGrantEventTypes.TokenUse, grant, new { inputTokens });
            return grant;
        }
    }

    public IReadOnlyList<HostedProcessingGrant> ListActive(DateTimeOffset? at = null)
    {
        var now = at ?? _clock.UtcNow;
        return ListAll().Where(g => g.IsActiveAt(now)).ToList();
    }

    public IReadOnlyList<HostedProcessingGrant> ListAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(GrantsDirectory)) return [];
            var list = new List<HostedProcessingGrant>();
            foreach (var path in Directory.EnumerateFiles(GrantsDirectory, "*.json"))
            {
                var text = AtomicFile.ReadAllTextIfExists(path);
                if (text is null) continue;
                var grant = JsonSerializer.Deserialize<HostedProcessingGrant>(text, RelayJson.Indented);
                if (grant is not null) list.Add(grant);
            }
            return list.OrderBy(g => g.CreatedAt).ToList();
        }
    }

    private void Save(HostedProcessingGrant grant)
    {
        lock (_gate) SaveUnlocked(grant);
    }

    private void SaveUnlocked(HostedProcessingGrant grant)
    {
        Directory.CreateDirectory(GrantsDirectory);
        AtomicFile.WriteAllText(GrantPath(grant.GrantId), JsonSerializer.Serialize(grant, RelayJson.Indented));
    }

    private HostedProcessingGrant? TryGetUnlocked(string grantId)
    {
        var text = AtomicFile.ReadAllTextIfExists(GrantPath(grantId));
        return text is null ? null : JsonSerializer.Deserialize<HostedProcessingGrant>(text, RelayJson.Indented);
    }

    private void AppendEvent(string type, HostedProcessingGrant grant, object? extra = null)
    {
        lock (_gate) AppendEventUnlocked(type, grant, extra);
    }

    private void AppendEventUnlocked(string type, HostedProcessingGrant grant, object? extra = null)
    {
        Directory.CreateDirectory(GrantsDirectory);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["eventId"] = Ulid.NewUlid(_clock.UtcNow),
            ["type"] = type,
            ["ts"] = _clock.UtcNow,
            ["grantId"] = grant.GrantId,
            ["scope"] = grant.Scope,
            ["sessionId"] = grant.SessionId,
            ["projectId"] = grant.ProjectId,
            ["tokensUsed"] = grant.TokensUsed,
            ["maximumInputTokenBudget"] = grant.MaximumInputTokenBudget,
            ["revokedAt"] = grant.RevokedAt,
        };
        if (extra is not null)
            payload["extra"] = extra;
        var line = JsonSerializer.Serialize(payload, RelayJson.Compact) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        using var stream = new FileStream(EventsPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

public static class HostedGrantEventTypes
{
    public const string Created = "hosted_grant.created";
    public const string Revoked = "hosted_grant.revoked";
    public const string TokenUse = "hosted_grant.token_use";
}

/// <summary>UI-facing hosted judgment status (no raw transcript).</summary>
public sealed record HostedJudgmentView(
    [property: JsonPropertyName("listeningIndependent")] bool ListeningIndependent,
    [property: JsonPropertyName("hasActiveGrant")] bool HasActiveGrant,
    [property: JsonPropertyName("activeGrantIds")] IReadOnlyList<string> ActiveGrantIds,
    [property: JsonPropertyName("tokensUsed")] int TokensUsed,
    [property: JsonPropertyName("tokenBudget")] int TokenBudget,
    [property: JsonPropertyName("jevStatus")] string JevStatus,
    [property: JsonPropertyName("detail")] string? Detail = null)
{
    public const string Disabled = "disabled";
    public const string Ready = "ready";
    public const string WaitingGrant = "waiting_grant";
    public const string Unavailable = "unavailable";
}
