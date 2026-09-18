using System.Text.Json.Serialization;

namespace Relay.Core.Privacy;

/// <summary>Classification of a source object for hosted-disclosure gating.</summary>
public static class SourceClassification
{
    public const string LocalOnly = "local_only";
    public const string HostedAllowedSession = "hosted_allowed_session";
    public const string HostedAllowedProject = "hosted_allowed_project";
    public const string Public = "public";

    public static readonly string[] All =
    [
        LocalOnly,
        HostedAllowedSession,
        HostedAllowedProject,
        Public,
    ];

    /// <summary>Lower rank = more restrictive.</summary>
    public static int Rank(string classification) => classification switch
    {
        LocalOnly => 0,
        HostedAllowedSession => 1,
        HostedAllowedProject => 2,
        Public => 3,
        _ => 0,
    };

    public static bool IsKnown(string? classification) =>
        !string.IsNullOrWhiteSpace(classification) && All.Contains(classification, StringComparer.Ordinal);

    /// <summary>Derived content inherits the most restrictive classification of every source used.</summary>
    public static string MostRestrictive(IEnumerable<string> classifications)
    {
        var list = classifications.Where(IsKnown).ToList();
        if (list.Count == 0) return LocalOnly;
        return list.OrderBy(Rank).First();
    }

    public static bool IsHostedEligible(string classification) =>
        classification is HostedAllowedSession or HostedAllowedProject or Public;
}

/// <summary>Allowed purposes on a hosted-processing grant.</summary>
public static class HostedPurposes
{
    public const string ConversationScreen = "conversation_screen";
    public const string AcronymDisambiguation = "acronym_disambiguation";
    public const string NoteVerification = "note_verification";
    public const string DirectRouting = "direct_routing";

    public static readonly string[] All =
    [
        ConversationScreen,
        AcronymDisambiguation,
        NoteVerification,
        DirectRouting,
    ];

    public static bool IsKnown(string? purpose) =>
        !string.IsNullOrWhiteSpace(purpose) && All.Contains(purpose, StringComparer.Ordinal);
}

public static class HostedProviders
{
    public const string TypeSafe = "typesafe";
}

public static class HostedGrantScopes
{
    public const string Session = "session";
    public const string Project = "project";
}

/// <summary>One durable hosted-processing grant. Covers later Jev calls within limits — not one card per inference.</summary>
public sealed class HostedProcessingGrant
{
    [JsonPropertyName("grantId")] public required string GrantId { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("projectId")] public string? ProjectId { get; init; }
    [JsonPropertyName("allowedProvider")] public string AllowedProvider { get; init; } = HostedProviders.TypeSafe;
    [JsonPropertyName("allowedSourceClassifications")] public List<string> AllowedSourceClassifications { get; init; } = [];
    [JsonPropertyName("allowedPurposes")] public List<string> AllowedPurposes { get; init; } = [];
    [JsonPropertyName("maximumInputTokenBudget")] public int MaximumInputTokenBudget { get; init; }
    [JsonPropertyName("tokensUsed")] public int TokensUsed { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt is not null;
    public int TokensRemaining => Math.Max(0, MaximumInputTokenBudget - TokensUsed);

    public void ValidateShape()
    {
        if (string.IsNullOrWhiteSpace(GrantId))
            throw new DisclosureException("grantId is required.");
        if (Scope is not (HostedGrantScopes.Session or HostedGrantScopes.Project))
            throw new DisclosureException("grant scope must be session or project.");
        if (Scope == HostedGrantScopes.Session && string.IsNullOrWhiteSpace(SessionId))
            throw new DisclosureException("session grant requires sessionId.");
        if (Scope == HostedGrantScopes.Project && string.IsNullOrWhiteSpace(ProjectId))
            throw new DisclosureException("project grant requires projectId.");
        if (!string.Equals(AllowedProvider, HostedProviders.TypeSafe, StringComparison.Ordinal))
            throw new DisclosureException("allowedProvider must be typesafe.");
        if (MaximumInputTokenBudget <= 0)
            throw new DisclosureException("maximumInputTokenBudget must be positive.");
        if (AllowedSourceClassifications.Count == 0)
            throw new DisclosureException("allowedSourceClassifications must not be empty.");
        if (AllowedPurposes.Count == 0)
            throw new DisclosureException("allowedPurposes must not be empty.");
        if (AllowedSourceClassifications.Any(c => !SourceClassification.IsKnown(c)))
            throw new DisclosureException("unknown source classification on grant.");
        if (AllowedSourceClassifications.Contains(SourceClassification.LocalOnly))
            throw new DisclosureException("local_only cannot be an allowed hosted classification.");
        if (AllowedPurposes.Any(p => !HostedPurposes.IsKnown(p)))
            throw new DisclosureException("unknown purpose on grant.");
    }

    public bool IsActiveAt(DateTimeOffset now)
    {
        if (IsRevoked) return false;
        if (ExpiresAt is { } exp && now >= exp) return false;
        return TokensRemaining > 0;
    }
}

public sealed class DisclosureException : Exception
{
    public string Category { get; }

    public DisclosureException(string message, string category = "not_authorized") : base(message)
    {
        Category = category;
    }
}
