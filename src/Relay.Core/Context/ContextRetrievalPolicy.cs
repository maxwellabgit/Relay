using Relay.Core.Evidence;

namespace Relay.Core.Context;

/// <summary>Hard bounds for context retrieval and assembly.</summary>
public static class ContextRetrievalPolicy
{
    public const int MaxCandidates = 20;
    public const int MaxRankedExcerpts = 8;
    public const int MaxStateChars = 24_000;

    /// <summary>Apply export restrictions before any hosted rerank — local_only never enters hosted packages.</summary>
    public static IReadOnlyList<ContentArtifact> FilterForHosted(IEnumerable<ContentArtifact> candidates)
        => candidates.Where(c => c.Restriction != ContentRestriction.LocalOnly).ToList();

    public static IReadOnlyList<T> CapCandidates<T>(IEnumerable<T> items)
        => items.Take(MaxCandidates).ToList();

    public static IReadOnlyList<T> CapRankedExcerpts<T>(IEnumerable<T> items)
        => items.Take(MaxRankedExcerpts).ToList();
}
