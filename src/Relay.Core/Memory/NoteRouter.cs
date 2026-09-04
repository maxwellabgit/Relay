using System.Text.RegularExpressions;
using Relay.Core.Notes;
using Relay.Core.Projects;
using Relay.Core.Search;

namespace Relay.Core.Memory;

public sealed record RoutingCandidate(string ProjectId, string Slug, string Name, double Confidence, IReadOnlyList<string> Reasons);

public sealed record RoutingResult(RoutingCandidate? Best, IReadOnlyList<RoutingCandidate> Candidates, string Summary)
{
    public double Confidence => Best?.Confidence ?? 0;
}

/// <summary>What the router knows about a project: identity words plus the vocabulary of its existing notes.</summary>
public sealed class ProjectProfile
{
    public required ProjectRecord Project { get; init; }
    public required HashSet<string> Vocabulary { get; init; }
    public required IReadOnlyList<string> Mentions { get; init; }   // name, slug, aliases — matched as whole words
    public int NoteCount { get; init; }

    public static ProjectProfile Build(ProjectRecord project)
    {
        var vocab = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        if (Directory.Exists(project.RootPath))
        {
            foreach (var (note, _) in ProjectNoteStore.ReadAll(project.RootPath).Notes)
            {
                if (note.Status == NoteStatus.Superseded) continue;
                foreach (var t in SearchIndex.Tokenize(note.Body)) vocab.Add(t);
                count++;
            }
        }
        var mentions = new List<string> { project.Name, project.Slug.Replace('-', ' ') };
        mentions.AddRange(project.Aliases);
        return new ProjectProfile { Project = project, Vocabulary = vocab, Mentions = mentions.Where(m => m.Trim().Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), NoteCount = count };
    }
}

/// <summary>
/// Deterministic routing confidence (memory rule 4). An explicit mention of the project's name,
/// slug or alias is strong evidence; vocabulary overlap with the project's existing notes is weak
/// evidence; a close runner-up makes the result ambiguous and caps the confidence so it lands in
/// Review instead of being filed. The thresholds live in settings and are the user's to move.
/// </summary>
public static class NoteRouter
{
    // An explicit name mention clears the default automatic threshold (0.75) on its own; an alias alone just meets it.
    public const double MentionWeight = 0.8;
    public const double AliasWeight = 0.75;
    public const double OverlapWeight = 0.4;
    public const double AmbiguityCap = 0.5;
    public const double SoleProjectFloor = 0.3;

    public static RoutingResult Route(string noteText, IReadOnlyList<ProjectProfile> profiles)
    {
        if (profiles.Count == 0) return new RoutingResult(null, [], "No active projects to route into.");
        var tokens = new HashSet<string>(SearchIndex.Tokenize(noteText), StringComparer.Ordinal);
        var candidates = new List<RoutingCandidate>();

        foreach (var profile in profiles)
        {
            var reasons = new List<string>();
            double score = 0;
            var mentioned = profile.Mentions.FirstOrDefault(m => Regex.IsMatch(noteText, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(m)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (mentioned is not null)
            {
                var isName = string.Equals(mentioned, profile.Project.Name, StringComparison.OrdinalIgnoreCase);
                score += isName ? MentionWeight : AliasWeight;
                reasons.Add($"mentions '{mentioned}'");
            }
            if (profile.Vocabulary.Count > 0 && tokens.Count > 0)
            {
                var overlap = tokens.Count(profile.Vocabulary.Contains);
                if (overlap > 0)
                {
                    var ratio = (double)overlap / tokens.Count;
                    score += Math.Min(OverlapWeight, ratio * OverlapWeight * 1.5);
                    reasons.Add($"{overlap} of {tokens.Count} words already appear in its notes");
                }
            }
            if (score > 0) candidates.Add(new RoutingCandidate(profile.Project.Id, profile.Project.Slug, profile.Project.Name, Math.Min(1, score), reasons));
        }

        if (candidates.Count == 0)
        {
            if (profiles.Count == 1)
            {
                var only = profiles[0].Project;
                var sole = new RoutingCandidate(only.Id, only.Slug, only.Name, SoleProjectFloor, ["the only active project, but the note does not mention it"]);
                return new RoutingResult(sole, [sole], $"No evidence for a project; '{only.Slug}' is the only candidate.");
            }
            return new RoutingResult(null, [], "The note does not mention any project and shares no vocabulary with one.");
        }

        var ordered = candidates.OrderByDescending(c => c.Confidence).ToList();
        var best = ordered[0];
        if (ordered.Count > 1 && ordered[1].Confidence >= best.Confidence - 0.1)
        {
            best = best with { Confidence = Math.Min(best.Confidence, AmbiguityCap), Reasons = [.. best.Reasons, $"ambiguous with '{ordered[1].Slug}'"] };
            ordered[0] = best;
        }
        return new RoutingResult(best, ordered, $"Best match '{best.Slug}' at {best.Confidence:0.00}: {string.Join(", ", best.Reasons)}");
    }
}

public sealed record DisputeFinding(NoteDocument Existing, double Similarity, string Reason);

/// <summary>
/// Contradiction detection (memory rule 5) without a model: two active decisions in the same
/// project about the same words, with different text, are a dispute. Both stay; the newer one is
/// written with status <c>disputed</c> and a link, and the user resolves it in Review.
/// </summary>
public static class DisputeDetector
{
    public const double SimilarityThreshold = 0.45;

    public static IReadOnlyList<DisputeFinding> Find(string newType, string newText, IEnumerable<NoteDocument> existing)
    {
        if (newType != NoteTypes.Decision) return [];
        var mine = new HashSet<string>(SearchIndex.Tokenize(newText), StringComparer.Ordinal);
        if (mine.Count < 2) return [];
        var findings = new List<DisputeFinding>();
        foreach (var note in existing)
        {
            if (note.Type != NoteTypes.Decision || note.Status is NoteStatus.Superseded or NoteStatus.Archived) continue;
            var theirs = new HashSet<string>(SearchIndex.Tokenize(note.Body), StringComparer.Ordinal);
            if (theirs.Count < 2) continue;
            var inter = mine.Count(theirs.Contains);
            var union = mine.Count + theirs.Count - inter;
            var jaccard = union == 0 ? 0 : (double)inter / union;
            if (jaccard >= SimilarityThreshold && !string.Equals(note.Body.Trim(), newText.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DisputeFinding(note, jaccard, $"both are decisions sharing {inter} significant word(s) (similarity {jaccard:0.00}) but say different things"));
            }
        }
        return findings.OrderByDescending(f => f.Similarity).ToList();
    }
}
