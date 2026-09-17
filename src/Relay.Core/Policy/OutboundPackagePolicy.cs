using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Evidence;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Policy;

public static class OutboundBlockReasons
{
    public const string BlockedContextRestriction = "blocked_context_restriction";
    public const string NoGrant = "no_grant";
    public const string GrantRevoked = "grant_revoked";
    public const string GrantExpired = "grant_expired";
    public const string PurposeNotAllowed = "purpose_not_allowed";
    public const string ProjectScopeMismatch = "project_scope_mismatch";
    public const string SessionScopeMismatch = "session_scope_mismatch";
    public const string SourceScopeMismatch = "source_scope_mismatch";
    public const string BudgetExceeded = "budget_exceeded";
    public const string HostedDisabled = "hosted_disabled";
    public const string RevokedBeforeDispatch = "revoked_before_dispatch";
}

/// <summary>Candidate content for an outbound hosted package.</summary>
public sealed class OutboundContentItem
{
    public required string ArtifactId { get; init; }
    public required string Role { get; init; } // state | query | question | criteria | instruction
    public bool Required { get; init; } = true;
}

/// <summary>Persisted dispatch intent after package approval.</summary>
public sealed class OutboundDispatchIntent
{
    [JsonPropertyName("intentId")] public required string IntentId { get; init; }
    [JsonPropertyName("grantId")] public required string GrantId { get; init; }
    [JsonPropertyName("grantRevocationVersion")] public long GrantRevocationVersion { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("purpose")] public required string Purpose { get; init; }
    [JsonPropertyName("packageHash")] public required string PackageHash { get; init; }
    [JsonPropertyName("reservationId")] public required string ReservationId { get; init; }
    [JsonPropertyName("includedArtifactIds")] public List<string> IncludedArtifactIds { get; set; } = [];
    [JsonPropertyName("excludedArtifactIds")] public List<string> ExcludedArtifactIds { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "queued";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("blockReason")] public string? BlockReason { get; set; }
}

public sealed record OutboundPrepareResult(
    bool Ok,
    string? BlockReason = null,
    OutboundDispatchIntent? Intent = null,
    byte[]? ApprovedBytes = null,
    string? PackageHash = null,
    BudgetReservation? Reservation = null);

/// <summary>
/// Outbound processing gate for every Jev / hosted reasoning / hosted search call
/// (and generated decision questions). Never auto-sends local-only content to obtain permission.
/// </summary>
public sealed class OutboundPackagePolicy
{
    private readonly EvidenceStore _evidence;
    private readonly ProvenanceGraph _provenance;
    private readonly HostedGrantStore _grants;
    private readonly BudgetReservations _budgets;
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly object _gate = new();

    /// <summary>When false, listening/capture may continue but zero hosted requests are sent.</summary>
    public bool HostedEnabled { get; set; }

    public OutboundPackagePolicy(
        EvidenceStore evidence,
        ProvenanceGraph provenance,
        HostedGrantStore grants,
        BudgetReservations budgets,
        DataRoot root,
        IClock clock,
        bool hostedEnabled = false)
    {
        _evidence = evidence;
        _provenance = provenance;
        _grants = grants;
        _budgets = budgets;
        _root = root;
        _clock = clock;
        HostedEnabled = hostedEnabled;
    }

    public string IntentsDirectory => Path.Combine(_root.GrantsDirectory, "intents");

    /// <summary>
    /// Full outbound pipeline. Steps:
    /// 1 construct payload, 2 include provenance, 3 check grant, 4 exclude restricted,
    /// 5 ensure remaining package satisfies required context, 6 reserve budget,
    /// 7 persist package hash + intent, 8 recheck revocation, 9 return approved bytes.
    /// </summary>
    public OutboundPrepareResult Prepare(
        string provider,
        string purpose,
        IReadOnlyList<OutboundContentItem> items,
        string? grantId = null,
        long estimatedInputTokens = 0,
        long estimatedSpendingMicros = 0)
    {
        if (!HostedEnabled)
            return new OutboundPrepareResult(false, OutboundBlockReasons.HostedDisabled);

        // 1–2: construct exact outbound payload with provenance of each item.
        var included = new List<(OutboundContentItem Item, ContentArtifact Artifact, string Text)>();
        var excluded = new List<string>();

        foreach (var item in items)
        {
            var artifact = _evidence.TryLoad(item.ArtifactId);
            if (artifact is null)
            {
                if (item.Required)
                    return new OutboundPrepareResult(false, OutboundBlockReasons.BlockedContextRestriction);
                continue;
            }

            // Provenance: effective restriction across lineage.
            var restriction = _provenance.EffectiveRestriction(item.ArtifactId);
            if (restriction == ContentRestriction.LocalOnly
                || artifact.Restriction == ContentRestriction.LocalOnly)
            {
                // 4: exclude restricted — never auto-send to obtain permission.
                excluded.Add(item.ArtifactId);
                if (item.Required)
                    return new OutboundPrepareResult(false, OutboundBlockReasons.BlockedContextRestriction);
                continue;
            }

            var text = _evidence.TryReadText(item.ArtifactId) ?? "";
            included.Add((item, artifact, text));
        }

        // 5: remaining package must still satisfy required context.
        if (items.Any(i => i.Required) && !items.Where(i => i.Required).All(i => included.Any(x => x.Item.ArtifactId == i.ArtifactId)))
            return new OutboundPrepareResult(false, OutboundBlockReasons.BlockedContextRestriction);

        if (included.Count == 0 && items.Count > 0)
            return new OutboundPrepareResult(false, OutboundBlockReasons.BlockedContextRestriction);

        var includedIds = included.Select(x => x.Artifact.ArtifactId).ToList();
        var requiredProjects = _provenance.RequiredProjectIds(includedIds);
        var requiredSessions = _provenance.RequiredSessionIds(includedIds);
        var sourceClosure = includedIds.SelectMany(_provenance.SourceClosure).Distinct(StringComparer.Ordinal).ToList();

        // 3: check current grant.
        var grant = ResolveGrant(provider, grantId, purpose, requiredProjects, requiredSessions, sourceClosure);
        if (grant.BlockReason is not null)
            return new OutboundPrepareResult(false, grant.BlockReason);

        var activeGrant = grant.Grant!;

        // Build exact payload bytes (deterministic).
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provider"] = provider,
            ["purpose"] = purpose,
            ["grantId"] = activeGrant.GrantId,
            ["items"] = included
                .OrderBy(x => x.Item.Role, StringComparer.Ordinal)
                .ThenBy(x => x.Artifact.ArtifactId, StringComparer.Ordinal)
                .Select(x => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["artifactId"] = x.Artifact.ArtifactId,
                    ["contentHash"] = x.Artifact.ContentHash,
                    ["role"] = x.Item.Role,
                    ["sourceRefs"] = x.Artifact.SourceRefs.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    ["projectIds"] = x.Artifact.ProjectIds.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    ["sessionIds"] = x.Artifact.SessionIds.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    ["text"] = x.Text,
                })
                .ToList(),
        };
        var json = JsonSerializer.Serialize(payload, RelayJson.Compact);
        var bytes = Encoding.UTF8.GetBytes(json);
        var packageHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var tokens = estimatedInputTokens > 0 ? estimatedInputTokens : EstimateTokens(bytes.Length);
        var spend = estimatedSpendingMicros > 0 ? estimatedSpendingMicros : tokens; // µ$ ≈ tokens under pricing-v1 estimate

        // 6: reserve budget atomically.
        var reservation = _budgets.TryReserve(activeGrant.GrantId, purpose, tokens, spend, packageHash);
        if (reservation is null)
            return new OutboundPrepareResult(false, OutboundBlockReasons.BudgetExceeded);

        // 7: persist package hash + dispatch intent.
        var intent = new OutboundDispatchIntent
        {
            IntentId = Ulid.NewUlid(_clock.UtcNow),
            GrantId = activeGrant.GrantId,
            GrantRevocationVersion = activeGrant.RevocationVersion,
            Provider = provider,
            Purpose = purpose,
            PackageHash = packageHash,
            ReservationId = reservation.ReservationId,
            IncludedArtifactIds = includedIds,
            ExcludedArtifactIds = excluded,
            Status = "queued",
            CreatedAt = _clock.UtcNow,
        };
        PersistIntent(intent);

        return new OutboundPrepareResult(true, Intent: intent, ApprovedBytes: bytes, PackageHash: packageHash, Reservation: reservation);
    }

    /// <summary>
    /// Step 8–9: recheck revocation immediately before dispatch; return approved bytes or block.
    /// Queued intents blocked by revoke/expiry never send.
    /// </summary>
    public OutboundPrepareResult Dispatch(string intentId)
    {
        lock (_gate)
        {
            var intent = TryLoadIntent(intentId);
            if (intent is null)
                return new OutboundPrepareResult(false, OutboundBlockReasons.NoGrant);

            if (!HostedEnabled)
            {
                intent.Status = "blocked";
                intent.BlockReason = OutboundBlockReasons.HostedDisabled;
                PersistIntent(intent);
                _budgets.Release(intent.ReservationId);
                return new OutboundPrepareResult(false, OutboundBlockReasons.HostedDisabled, intent);
            }

            var grant = _grants.TryLoad(intent.GrantId);
            if (grant is null)
            {
                intent.Status = "blocked";
                intent.BlockReason = OutboundBlockReasons.NoGrant;
                PersistIntent(intent);
                _budgets.Release(intent.ReservationId);
                return new OutboundPrepareResult(false, OutboundBlockReasons.NoGrant, intent);
            }

            if (grant.Revoked || grant.RevocationVersion != intent.GrantRevocationVersion)
            {
                intent.Status = "blocked";
                intent.BlockReason = OutboundBlockReasons.RevokedBeforeDispatch;
                PersistIntent(intent);
                _budgets.Release(intent.ReservationId);
                return new OutboundPrepareResult(false, OutboundBlockReasons.RevokedBeforeDispatch, intent);
            }

            if (grant.ExpiresAt <= _clock.UtcNow)
            {
                intent.Status = "blocked";
                intent.BlockReason = OutboundBlockReasons.GrantExpired;
                PersistIntent(intent);
                _budgets.Release(intent.ReservationId);
                return new OutboundPrepareResult(false, OutboundBlockReasons.GrantExpired, intent);
            }

            // Reconstruct approved bytes from included artifacts (exact package).
            var items = intent.IncludedArtifactIds
                .Select(id => new OutboundContentItem { ArtifactId = id, Role = "state", Required = true })
                .ToList();

            // Re-verify no restricted content slipped in.
            foreach (var id in intent.IncludedArtifactIds)
            {
                if (_provenance.EffectiveRestriction(id) == ContentRestriction.LocalOnly)
                {
                    intent.Status = "blocked";
                    intent.BlockReason = OutboundBlockReasons.BlockedContextRestriction;
                    PersistIntent(intent);
                    _budgets.Release(intent.ReservationId);
                    return new OutboundPrepareResult(false, OutboundBlockReasons.BlockedContextRestriction, intent);
                }
            }

            intent.Status = "dispatched";
            PersistIntent(intent);

            // Caller sends approved bytes; we return the package hash for verification.
            return new OutboundPrepareResult(true, Intent: intent, PackageHash: intent.PackageHash,
                Reservation: _budgets.TryLoad(intent.ReservationId));
        }
    }

    /// <summary>
    /// Hosted request counter for tests — increments only on successful Dispatch.
    /// </summary>
    public int HostedRequestCount { get; private set; }

    public OutboundPrepareResult PrepareAndDispatch(
        string provider,
        string purpose,
        IReadOnlyList<OutboundContentItem> items,
        string? grantId = null,
        long estimatedInputTokens = 0,
        long estimatedSpendingMicros = 0,
        Action<byte[]>? send = null)
    {
        var prepared = Prepare(provider, purpose, items, grantId, estimatedInputTokens, estimatedSpendingMicros);
        if (!prepared.Ok || prepared.Intent is null)
            return prepared;

        var dispatched = Dispatch(prepared.Intent.IntentId);
        if (!dispatched.Ok)
            return dispatched;

        if (prepared.ApprovedBytes is not null)
        {
            send?.Invoke(prepared.ApprovedBytes);
            HostedRequestCount++;
        }

        return new OutboundPrepareResult(true, Intent: dispatched.Intent, ApprovedBytes: prepared.ApprovedBytes,
            PackageHash: prepared.PackageHash, Reservation: prepared.Reservation);
    }

    private (HostedProcessingGrant? Grant, string? BlockReason) ResolveGrant(
        string provider,
        string? grantId,
        string purpose,
        IReadOnlyList<string> requiredProjects,
        IReadOnlyList<string> requiredSessions,
        IReadOnlyList<string> sourceClosure)
    {
        HostedProcessingGrant? grant;
        if (grantId is not null)
        {
            grant = _grants.TryLoad(grantId);
            if (grant is null) return (null, OutboundBlockReasons.NoGrant);
        }
        else
        {
            grant = _grants.ListActive(provider).FirstOrDefault();
            if (grant is null) return (null, OutboundBlockReasons.NoGrant);
        }

        if (grant.Revoked) return (null, OutboundBlockReasons.GrantRevoked);
        if (grant.ExpiresAt <= _clock.UtcNow) return (null, OutboundBlockReasons.GrantExpired);
        if (!string.Equals(grant.Provider, provider, StringComparison.Ordinal))
            return (null, OutboundBlockReasons.NoGrant);
        if (grant.AllowedPurposes.Count > 0
            && !grant.AllowedPurposes.Contains(purpose, StringComparer.Ordinal)
            && !grant.AllowedPurposes.Contains("*", StringComparer.Ordinal))
            return (null, OutboundBlockReasons.PurposeNotAllowed);

        // Cross-project: grant must cover every included source's project.
        if (grant.ProjectIds.Count > 0)
        {
            foreach (var projectId in requiredProjects)
            {
                if (!grant.ProjectIds.Contains(projectId, StringComparer.Ordinal)
                    && !grant.ProjectIds.Contains("*", StringComparer.Ordinal))
                    return (null, OutboundBlockReasons.ProjectScopeMismatch);
            }
        }

        if (grant.SessionIds.Count > 0 && requiredSessions.Count > 0)
        {
            foreach (var sessionId in requiredSessions)
            {
                if (!grant.SessionIds.Contains(sessionId, StringComparer.Ordinal)
                    && !grant.SessionIds.Contains("*", StringComparer.Ordinal))
                    return (null, OutboundBlockReasons.SessionScopeMismatch);
            }
        }

        if (grant.AllowedSourceScope.Count > 0
            && !grant.AllowedSourceScope.Contains("*", StringComparer.Ordinal))
        {
            foreach (var source in sourceClosure)
            {
                if (!grant.AllowedSourceScope.Any(scope =>
                        string.Equals(scope, source, StringComparison.Ordinal)
                        || source.StartsWith(scope, StringComparison.Ordinal)))
                    return (null, OutboundBlockReasons.SourceScopeMismatch);
            }
        }

        return (grant, null);
    }

    private void PersistIntent(OutboundDispatchIntent intent)
    {
        Directory.CreateDirectory(IntentsDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(IntentsDirectory, intent.IntentId + ".json"),
            JsonSerializer.Serialize(intent, RelayJson.Indented));
    }

    public OutboundDispatchIntent? TryLoadIntent(string intentId)
    {
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(IntentsDirectory, intentId + ".json"));
        return text is null ? null : JsonSerializer.Deserialize<OutboundDispatchIntent>(text, RelayJson.Indented);
    }

    private static long EstimateTokens(int byteLength) => Math.Max(1, byteLength / 4);
}
