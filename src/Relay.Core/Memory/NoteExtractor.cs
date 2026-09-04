using System.Text.RegularExpressions;
using Relay.Core.Notes;
using Relay.Core.Search;

namespace Relay.Core.Memory;

/// <summary>One atomic note cut from a capture, with the exact half-open character range it came from.</summary>
public sealed record ExtractedNote(string Type, string Text, int Start, int End, string? Topic);

/// <summary>
/// Deterministic extraction (memory rule 2): splits a capture into atomic statements along
/// paragraph and sentence boundaries, classifies each by cue words, and keeps exact offsets so
/// every note cites the words that produced it. It never rewrites text. A model may later
/// propose better splits; it cannot replace the spans.
/// </summary>
public static partial class NoteExtractor
{
    private const int MinSegmentChars = 25;

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""“(])", RegexOptions.CultureInvariant)] private static partial Regex SentenceBoundary();
    [GeneratedRegex(@"\b(decided|decision|we will|we'll|going with|go with|settled on|agreed|final answer|from now on|let's use|we are using|we're using|switching to)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DecisionCue();
    [GeneratedRegex(@"\b(todo|to-do|to do|need to|needs to|have to|must|should|remember to|don't forget|follow up|action item|next step|by (monday|tuesday|wednesday|thursday|friday|tomorrow|next week))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex TaskCue();
    [GeneratedRegex(@"\b(idea|what if|maybe we could|could we|we could|might be worth|consider|thought:|brainstorm|possibility)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex IdeaCue();
    [GeneratedRegex(@"(https?://|www\.)|\b(see|according to|source|link|article|paper|book|documentation|docs)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ReferenceCue();
    [GeneratedRegex(@"^\s*(what|when|where|who|why|how|which|is|are|can|could|should|do|does|did|will|would)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex QuestionStart();

    public static IReadOnlyList<ExtractedNote> Extract(string text)
    {
        var notes = new List<ExtractedNote>();
        if (string.IsNullOrWhiteSpace(text)) return notes;

        foreach (var (pStart, pEnd) in Paragraphs(text))
        {
            var paragraph = text[pStart..pEnd];
            var segments = new List<(int Start, int End)>();
            var cursor = 0;
            foreach (Match m in SentenceBoundary().Matches(paragraph))
            {
                segments.Add((cursor, m.Index));
                cursor = m.Index + m.Length;
            }
            segments.Add((cursor, paragraph.Length));

            // Merge fragments that are too short to stand alone into their successor.
            var merged = new List<(int Start, int End)>();
            foreach (var seg in segments)
            {
                if (merged.Count > 0 && (merged[^1].End - merged[^1].Start) < MinSegmentChars)
                    merged[^1] = (merged[^1].Start, seg.End);
                else merged.Add(seg);
            }
            if (merged.Count > 1 && (merged[^1].End - merged[^1].Start) < MinSegmentChars)
            {
                merged[^2] = (merged[^2].Start, merged[^1].End);
                merged.RemoveAt(merged.Count - 1);
            }

            foreach (var (s, e) in merged)
            {
                var (ts, te) = TrimRange(paragraph, s, e);
                if (te <= ts) continue;
                var body = paragraph[ts..te];
                notes.Add(new ExtractedNote(Classify(body), body, pStart + ts, pStart + te, Topic(body)));
            }
        }
        return notes;
    }

    private static IEnumerable<(int Start, int End)> Paragraphs(string text)
    {
        var start = 0;
        foreach (Match m in Regex.Matches(text, @"\r?\n\s*\r?\n"))
        {
            if (m.Index > start) yield return (start, m.Index);
            start = m.Index + m.Length;
        }
        if (start < text.Length) yield return (start, text.Length);
    }

    private static (int Start, int End) TrimRange(string s, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(s[start])) start++;
        while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
        return (start, end);
    }

    public static string Classify(string body)
    {
        var trimmed = body.TrimEnd();
        if (trimmed.EndsWith('?') || (QuestionStart().IsMatch(trimmed) && trimmed.Contains('?'))) return NoteTypes.Question;
        if (DecisionCue().IsMatch(trimmed)) return NoteTypes.Decision;
        if (TaskCue().IsMatch(trimmed)) return NoteTypes.Task;
        if (ReferenceCue().IsMatch(trimmed)) return NoteTypes.Reference;
        if (IdeaCue().IsMatch(trimmed)) return NoteTypes.Idea;
        return NoteTypes.Fact;
    }

    /// <summary>A short, deterministic label: the first four significant tokens.</summary>
    public static string? Topic(string body)
    {
        var tokens = SearchIndex.Tokenize(body).Distinct().Take(4).ToList();
        return tokens.Count == 0 ? null : string.Join(" ", tokens);
    }
}
