using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Evidence;
using Relay.Core.Judgments;
using Relay.Core.Usage;

namespace Relay.Core.Processes;

/// <summary>§10 production workflow helpers: recall, plan impact, research claims, improvement.</summary>
public static class ProductionWorkflows
{
    public const string RecallFact = "recall_fact";
    public const string CheckPlanImpact = "check_plan_impact";
    public const string ResearchClaim = "research_claim";
    public const string PrepareImprovement = "prepare_improvement";

    public static string DirectoryPath(string repoOrDataRoot)
        => Path.Combine(repoOrDataRoot, "workflows", "v1");
}

/// <summary>
/// Recall policy: newer statements are not auto-accepted facts.
/// Authority / explicit revision required; otherwise conflict stands.
/// </summary>
public static class RecallFactPolicy
{
    public sealed record RecallOutcome(
        bool Answered,
        string? Answer,
        IReadOnlyList<string> ProvenanceRefs,
        IReadOnlyList<string> ConflictIds,
        string? BlockReason);

    public static RecallOutcome Resolve(
        IReadOnlyList<(string StatementId, string Text, DateTimeOffset At, string? Authority)> statements,
        DecisionInterpretation? revisionRelation,
        DecisionInterpretation? claimRelation)
    {
        if (statements.Count == 0)
            return new RecallOutcome(false, null, [], [], "no_candidates");

        // Newest first for inspection — but never auto-accept solely by recency.
        var ordered = statements.OrderByDescending(s => s.At).ToList();
        var newest = ordered[0];

        if (revisionRelation?.ChosenOption == "conflict"
            || (revisionRelation?.ConflictIds.Count ?? 0) > 0)
        {
            return new RecallOutcome(false, null,
                ordered.Select(s => "statement:" + s.StatementId).ToList(),
                revisionRelation?.ConflictIds.Count > 0
                    ? revisionRelation.ConflictIds
                    : ordered.Select(s => s.StatementId).Take(2).ToList(),
                "unresolved_conflict");
        }

        if (revisionRelation?.ChosenOption == "revision"
            && !string.IsNullOrEmpty(newest.Authority))
        {
            return new RecallOutcome(true, newest.Text,
                ["statement:" + newest.StatementId, "authority:" + newest.Authority],
                [], null);
        }

        // Compatible / unclear / no authority: do not treat newer as fact.
        if (ordered.Count > 1 && string.IsNullOrEmpty(newest.Authority))
        {
            return new RecallOutcome(false, null,
                ordered.Select(s => "statement:" + s.StatementId).ToList(),
                ordered.Take(2).Select(s => s.StatementId).ToList(),
                "newer_not_auto_accepted");
        }

        if (claimRelation?.ChosenOption is "contradicts" or "insufficient" or "unrelated")
            return new RecallOutcome(false, null,
                ["statement:" + newest.StatementId], [], "claim_not_supported");

        if (claimRelation?.ChosenOption == "supports" && claimRelation.Polarity == DecisionPolarity.Positive)
        {
            return new RecallOutcome(true, newest.Text,
                ["statement:" + newest.StatementId], [], null);
        }

        return new RecallOutcome(true, newest.Text,
            ["statement:" + newest.StatementId], [], null);
    }
}

/// <summary>Plan-impact policy: disputed dates block consequential operations.</summary>
public static class PlanImpactPolicy
{
    public static bool BlocksConsequentialOps(bool dateDisputed) => dateDisputed;

    public static string? Evaluate(
        bool dateDisputed,
        IReadOnlyList<DecisionInterpretation> criteria)
    {
        if (dateDisputed) return "disputed_date";
        if (!DecisionPolicy.AllCriteriaSatisfied(criteria)) return "criterion_unsatisfied";
        return null;
    }
}

/// <summary>Exact arithmetic in code — never delegated to model judgment.</summary>
public static class ExactArithmetic
{
    public static bool TryAdd(string a, string b, out decimal result, out string? error)
    {
        error = null;
        result = 0;
        if (!decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x)
            || !decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y))
        {
            error = "non_numeric";
            return false;
        }
        result = x + y;
        return true;
    }

    public static bool TryMultiply(string a, string b, out decimal result, out string? error)
    {
        error = null;
        result = 0;
        if (!decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x)
            || !decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y))
        {
            error = "non_numeric";
            return false;
        }
        result = x * y;
        return true;
    }
}

/// <summary>Research claim support: validate selectors/hashes; never auto-attach all inputs as cited.</summary>
public static class ResearchClaimPolicy
{
    public sealed record ExcerptRef(
        string ArtifactId,
        string ContentHash,
        string Text,
        int StartOffset,
        int EndOffset,
        string? Selector = null);

    public static bool ValidateExcerpt(ExcerptRef excerpt, string sourceText, out string? error)
    {
        error = null;
        if (excerpt.StartOffset < 0 || excerpt.EndOffset < excerpt.StartOffset
            || excerpt.EndOffset > sourceText.Length)
        {
            error = "invalid_selector";
            return false;
        }
        var slice = sourceText[excerpt.StartOffset..excerpt.EndOffset];
        if (!string.Equals(slice, excerpt.Text, StringComparison.Ordinal))
        {
            error = "text_mismatch";
            return false;
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText)));
        if (!string.Equals(hash, excerpt.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            error = "hash_mismatch";
            return false;
        }
        return true;
    }

    public static bool AllowClaim(DecisionInterpretation claimSupport, IReadOnlyList<string> explicitCitationIds)
    {
        if (explicitCitationIds.Count == 0) return false;
        return DecisionPolicy.AllowClaim(claimSupport);
    }

    /// <summary>Do not treat every permitted input as a citation.</summary>
    public static IReadOnlyList<string> CitationsOnly(IEnumerable<string> permittedInputIds, IEnumerable<string> explicitCitationIds)
    {
        var permitted = new HashSet<string>(permittedInputIds, StringComparer.Ordinal);
        return explicitCitationIds.Where(permitted.Contains).Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>Improvement trigger: explicit request OR ≥3 supported occurrences across ≥2 sessions.</summary>
public static class ImprovementTriggerPolicy
{
    public const int MinOccurrences = 3;
    public const int MinSessions = 2;

    public static bool ShouldPrepare(
        bool explicitRequest,
        IReadOnlyList<FrictionEvidence> evidence,
        out string reason)
    {
        if (explicitRequest)
        {
            reason = "explicit_request";
            return true;
        }

        var supported = evidence.Where(e => e.Count >= 1).ToList();
        var total = supported.Sum(e => Math.Max(1, e.Count));
        var sessions = supported
            .Select(e => e.SessionId ?? e.CaseId)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (total >= MinOccurrences && sessions >= MinSessions)
        {
            reason = "threshold_met";
            return true;
        }

        reason = "insufficient_evidence";
        return false;
    }

    public static bool MayBuild(bool hasGrant) => hasGrant;
}
