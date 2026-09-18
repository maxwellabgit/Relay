using System.Text.RegularExpressions;
using Relay.Core.Memory;

namespace Relay.Core.Capabilities;

public sealed class AcronymCandidate
{
    public required string CandidateId { get; init; }
    public required string Acronym { get; init; }
    public required string Expansion { get; init; }
    public required string Scope { get; init; }
    public string? ProjectId { get; init; }
    public string? EntryId { get; init; }
}

public static class AcronymCandidateBuilder
{
    private static readonly Regex TokenRegex = new(@"\b[A-Z][A-Z0-9]{1,5}\b", RegexOptions.Compiled);

    public static IReadOnlyList<string> ExtractTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return TokenRegex.Matches(text)
            .Select(m => GlossaryStore.Normalize(m.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string? PrimaryToken(string text, string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            return GlossaryStore.Normalize(preferred);
        var tokens = ExtractTokens(text);
        return tokens.Count == 0 ? null : tokens[0];
    }

    /// <summary>
    /// Ordered candidates: project glossary first, then global. Dedup by expansion (ordinal ignore-case).
    /// </summary>
    public static IReadOnlyList<AcronymCandidate> Build(
        string acronym,
        GlossaryStore store,
        string? projectId,
        string? projectRoot)
    {
        var key = GlossaryStore.Normalize(acronym);
        var result = new List<AcronymCandidate>();
        var seenExpansions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(GlossaryEntry entry)
        {
            if (!string.Equals(GlossaryStore.Normalize(entry.Acronym), key, StringComparison.Ordinal))
                return;
            if (!seenExpansions.Add(entry.Expansion)) return;
            var scope = entry.Scope;
            var id = scope == GlossaryScopes.Project
                ? $"project:{entry.Id}"
                : $"global:{entry.Id}";
            result.Add(new AcronymCandidate
            {
                CandidateId = id,
                Acronym = key,
                Expansion = entry.Expansion,
                Scope = scope,
                ProjectId = entry.ProjectId ?? projectId,
                EntryId = entry.Id,
            });
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            foreach (var e in store.LoadProject(projectRoot)
                         .Where(e => e.Scope == GlossaryScopes.Project)
                         .OrderBy(e => e.Id, StringComparer.Ordinal))
                Add(e);
        }

        foreach (var e in store.LoadGlobal().OrderBy(e => e.Id, StringComparer.Ordinal))
            Add(e);

        return result;
    }

    public static AcronymCandidate? ExactProjectOnly(IReadOnlyList<AcronymCandidate> candidates)
    {
        var project = candidates.Where(c => c.Scope == GlossaryScopes.Project).ToList();
        return project.Count == 1 ? project[0] : null;
    }
}
