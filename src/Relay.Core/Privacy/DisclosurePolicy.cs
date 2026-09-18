using Relay.Core.Cases;
using Relay.Core.Judgments;

namespace Relay.Core.Privacy;

/// <summary>
/// Gates hosted judgment dispatch. Rejects the entire request if any necessary source is
/// excluded — never silently omits evidence. Audit metadata carries grant ID and source hashes only.
/// </summary>
public sealed class DisclosurePolicy
{
    private readonly HostedGrantStore _grants;
    private readonly ObjectStore _objects;
    private readonly Func<DateTimeOffset> _now;

    public DisclosurePolicy(HostedGrantStore grants, ObjectStore objects, Func<DateTimeOffset> now)
    {
        _grants = grants;
        _objects = objects;
        _now = now;
    }

    /// <summary>
    /// Authorize a judgment request. On success returns the grant to use and resolved source refs.
    /// Conservative token estimate must fit remaining budget.
    /// </summary>
    public DisclosureDecision Authorize(
        JudgmentRequest request,
        string purpose,
        string provider,
        string? sessionId,
        string? projectId,
        int conservativeInputTokenEstimate)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HostedPurposes.IsKnown(purpose))
            throw new DisclosureException($"Unknown purpose '{purpose}'.", "validation");
        if (string.IsNullOrWhiteSpace(provider))
            throw new DisclosureException("provider is required.", "validation");
        if (conservativeInputTokenEstimate < 0)
            throw new DisclosureException("token estimate must be non-negative.", "validation");

        var refs = request.SourceObjectRefs;
        if (refs.Count == 0)
            throw new DisclosureException("Judgment request requires source object references.", "validation");

        var resolved = new List<ResolvedSourceRef>();
        foreach (var sourceRef in refs)
        {
            var classification = ResolveClassification(sourceRef);
            if (classification == SourceClassification.LocalOnly)
            {
                throw new DisclosureException(
                    "A required source is local_only; the complete request is blocked.",
                    "not_authorized");
            }

            if (!SourceClassification.IsHostedEligible(classification))
            {
                throw new DisclosureException(
                    "A required source classification cannot be disclosed.",
                    "not_authorized");
            }

            resolved.Add(new ResolvedSourceRef(sourceRef.ObjectId, sourceRef.Sha256, classification));
        }

        HostedProcessingGrant? grant = null;
        if (!string.IsNullOrWhiteSpace(request.DisclosureGrantId))
        {
            grant = _grants.TryGet(request.DisclosureGrantId);
            if (grant is null)
                throw new DisclosureException("Disclosure grant not found.", "not_authorized");
            ValidateGrant(grant, purpose, provider, sessionId, projectId, resolved, conservativeInputTokenEstimate);
        }
        else
        {
            grant = FindApplicableGrant(purpose, provider, sessionId, projectId, resolved, conservativeInputTokenEstimate);
            if (grant is null)
                throw new DisclosureException("No applicable hosted-processing grant.", "not_authorized");
            ValidateGrant(grant, purpose, provider, sessionId, projectId, resolved, conservativeInputTokenEstimate);
        }

        return new DisclosureDecision(
            grant,
            resolved,
            Audit: new DisclosureAudit(
                grant.GrantId,
                purpose,
                provider,
                resolved.Select(r => r.Sha256).OrderBy(h => h, StringComparer.Ordinal).ToArray(),
                conservativeInputTokenEstimate));
    }

    /// <summary>Classification of derived content from source classifications.</summary>
    public static string ClassifyDerived(IEnumerable<string> sourceClassifications) =>
        SourceClassification.MostRestrictive(sourceClassifications);

    private string ResolveClassification(JudgmentSourceRef sourceRef)
    {
        if (!string.IsNullOrWhiteSpace(sourceRef.Classification))
        {
            if (!SourceClassification.IsKnown(sourceRef.Classification))
                throw new DisclosureException("Unknown source classification on request ref.", "validation");
            return sourceRef.Classification!;
        }

        var fromStore = _objects.TryGetClassification(sourceRef.ObjectId);
        if (fromStore is null)
            throw new DisclosureException("Source object classification is missing.", "validation");
        if (!SourceClassification.IsKnown(fromStore))
            throw new DisclosureException("Stored source classification is invalid.", "validation");
        return fromStore;
    }

    private HostedProcessingGrant? FindApplicableGrant(
        string purpose,
        string provider,
        string? sessionId,
        string? projectId,
        IReadOnlyList<ResolvedSourceRef> sources,
        int conservativeInputTokenEstimate)
    {
        var now = _now();
        DisclosureException? last = null;
        foreach (var candidate in _grants.ListAll().OrderByDescending(g => g.CreatedAt))
        {
            try
            {
                ValidateGrant(candidate, purpose, provider, sessionId, projectId, sources, conservativeInputTokenEstimate);
                return candidate;
            }
            catch (DisclosureException ex)
            {
                last = ex;
            }
        }

        if (last is not null)
            throw last;
        return null;
    }

    private void ValidateGrant(
        HostedProcessingGrant grant,
        string purpose,
        string provider,
        string? sessionId,
        string? projectId,
        IReadOnlyList<ResolvedSourceRef> sources,
        int conservativeInputTokenEstimate)
    {
        grant.ValidateShape();
        var now = _now();
        if (grant.IsRevoked)
            throw new DisclosureException("Hosted grant is revoked.", "not_authorized");
        if (grant.ExpiresAt is { } exp && now >= exp)
            throw new DisclosureException("Hosted grant is expired.", "not_authorized");
        if (grant.TokensRemaining <= 0 || conservativeInputTokenEstimate > grant.TokensRemaining)
            throw new DisclosureException("Hosted grant token budget is exhausted.", "not_authorized");
        if (!string.Equals(provider, grant.AllowedProvider, StringComparison.Ordinal))
            throw new DisclosureException("Request provider does not match the grant.", "not_authorized");
        if (!string.Equals(grant.AllowedProvider, HostedProviders.TypeSafe, StringComparison.Ordinal))
            throw new DisclosureException("Grant provider is not typesafe.", "not_authorized");
        if (!grant.AllowedPurposes.Contains(purpose, StringComparer.Ordinal))
            throw new DisclosureException("Grant does not allow this purpose.", "not_authorized");

        if (grant.Scope == HostedGrantScopes.Session)
        {
            if (string.IsNullOrWhiteSpace(sessionId) ||
                !string.Equals(grant.SessionId, sessionId, StringComparison.Ordinal))
                throw new DisclosureException("Session grant cannot authorize another session.", "not_authorized");
        }
        else if (grant.Scope == HostedGrantScopes.Project)
        {
            if (string.IsNullOrWhiteSpace(projectId) ||
                !string.Equals(grant.ProjectId, projectId, StringComparison.Ordinal))
                throw new DisclosureException("Project grant cannot authorize another project.", "not_authorized");
        }

        foreach (var source in sources)
        {
            if (!grant.AllowedSourceClassifications.Contains(source.Classification, StringComparer.Ordinal))
            {
                throw new DisclosureException(
                    "Grant does not allow a required source classification; the complete request is blocked.",
                    "not_authorized");
            }
        }
    }
}

public sealed record ResolvedSourceRef(string ObjectId, string Sha256, string Classification);

public sealed record DisclosureAudit(
    string GrantId,
    string Purpose,
    string Provider,
    IReadOnlyList<string> SourceHashes,
    int ConservativeInputTokenEstimate);

public sealed record DisclosureDecision(
    HostedProcessingGrant Grant,
    IReadOnlyList<ResolvedSourceRef> Sources,
    DisclosureAudit Audit);
