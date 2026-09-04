using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Projects;

namespace Relay.Core.Search;

public sealed record SearchHit(string Kind, string Id, string? ProjectId, string? ProjectSlug, string Type, string Excerpt, double Score, SourceSpan? Span, DateTimeOffset At, string Text, string Status = "active");

/// <summary>
/// A rebuildable in-memory projection over everything Relay may cite: stored captures (the raw
/// words), staging draft notes, and canonical project notes. Exact-term matching with a phrase
/// bonus; no embeddings, no network. Every hit carries the ledger span that lets the answer
/// point at the original words (memory rule 7).
/// </summary>
public sealed class SearchIndex
{
    public const string CaptureKind = "capture";
    public const string DraftKind = "draft";
    public const string NoteKind = "note";

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were", "be", "it", "this", "that", "with", "as", "at", "by",
        "i", "we", "you", "my", "our", "me", "about", "did", "do", "does", "what", "when", "where", "who", "why", "how", "say", "said", "tell", "show",
        "recall", "find", "search", "remember", "know", "have", "has", "had", "from", "into", "not", "no", "yes", "up", "so", "if", "then", "there",
    };

    private sealed record Doc(string Kind, string Id, string? ProjectId, string? ProjectSlug, string Type, string Text, DateTimeOffset At, string? EventId, int SpanOffset, HashSet<string> Tokens, string Status = "active");

    private readonly List<Doc> _docs = new();

    public int Count => _docs.Count;

    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) current.Append(ch);
            else if (current.Length > 0) { Flush(); }
        }
        if (current.Length > 0) Flush();
        return tokens;

        void Flush()
        {
            var t = current.ToString();
            current.Clear();
            if (t.Length >= 2 && !StopWords.Contains(t)) tokens.Add(t);
        }
    }

    public void IndexCapture(LedgerRecord record)
    {
        var text = record.DataString("text");
        if (record.Type != EventTypes.CaptureCommitted || string.IsNullOrWhiteSpace(text)) return;
        Replace(new Doc(CaptureKind, record.DataString("captureId") ?? record.Id, null, null, record.DataString("mode") ?? "note", text, record.Timestamp, record.Id, 0, new HashSet<string>(Tokenize(text))));
    }

    public void IndexDraft(DraftNote note)
    {
        var span = note.Spans.FirstOrDefault();
        Replace(new Doc(DraftKind, note.NoteId, note.ProjectId, null, note.Type, note.Text, note.CreatedAt, span?.EventId, span?.Start ?? 0, new HashSet<string>(Tokenize(note.Text))));
    }

    public void IndexNote(NoteDocument note, ProjectRecord project)
    {
        var span = note.Spans.FirstOrDefault();
        Replace(new Doc(NoteKind, note.Id, project.Id, project.Slug, note.Type, note.Body, note.Created, span?.EventId, span?.Start ?? 0, new HashSet<string>(Tokenize(note.Body)), note.Status));
    }

    public void Remove(string kind, string id) => _docs.RemoveAll(d => d.Kind == kind && d.Id == id);

    public void RemoveProject(string projectId) => _docs.RemoveAll(d => d.ProjectId == projectId && d.Kind == NoteKind);

    private void Replace(Doc doc)
    {
        _docs.RemoveAll(d => d.Kind == doc.Kind && d.Id == doc.Id);
        _docs.Add(doc);
    }

    public IReadOnlyList<SearchHit> Search(string query, string? projectId = null, int limit = 8, IEnumerable<string>? kinds = null, string? excludeId = null)
    {
        var terms = Tokenize(query).Distinct().ToList();
        var phrase = query.Trim().ToLowerInvariant();
        if (terms.Count == 0 && phrase.Length < 2) return [];
        var kindFilter = kinds is null ? null : new HashSet<string>(kinds, StringComparer.Ordinal);

        var hits = new List<SearchHit>();
        foreach (var doc in _docs)
        {
            if (projectId is not null && doc.ProjectId != projectId) continue;
            if (kindFilter is not null && !kindFilter.Contains(doc.Kind)) continue;
            if (excludeId is not null && doc.Id == excludeId) continue; // the instruction asking the question is not evidence
            var matched = terms.Count(doc.Tokens.Contains);
            var lower = doc.Text.ToLowerInvariant();
            var phraseIndex = phrase.Length >= 3 ? lower.IndexOf(phrase, StringComparison.Ordinal) : -1;
            if (matched == 0 && phraseIndex < 0) continue;
            var score = (terms.Count == 0 ? 0 : (double)matched / terms.Count) + (phraseIndex >= 0 ? 0.5 : 0);
            if (doc.Status == NoteStatus.Superseded) score -= 0.3; // still findable, never erased, ranked below current conclusions

            var (start, end) = phraseIndex >= 0 ? (phraseIndex, phraseIndex + phrase.Length) : FirstTermRange(lower, terms);
            var excerpt = Excerpt(doc.Text, start, end);
            var span = doc.EventId is null ? null : new SourceSpan(doc.EventId, doc.SpanOffset + start, doc.SpanOffset + end);
            hits.Add(new SearchHit(doc.Kind, doc.Id, doc.ProjectId, doc.ProjectSlug, doc.Type, excerpt, score, span, doc.At, doc.Text, doc.Status));
        }
        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.At).Take(limit).ToList();
    }

    private static (int Start, int End) FirstTermRange(string lower, IReadOnlyList<string> terms)
    {
        var best = (Start: 0, End: Math.Min(lower.Length, 40));
        var bestIndex = int.MaxValue;
        foreach (var term in terms)
        {
            var i = lower.IndexOf(term, StringComparison.Ordinal);
            if (i >= 0 && i < bestIndex) { bestIndex = i; best = (i, i + term.Length); }
        }
        return best;
    }

    private static string Excerpt(string text, int start, int end)
    {
        const int Window = 80;
        var from = Math.Max(0, start - Window);
        var to = Math.Min(text.Length, end + Window);
        var slice = text[from..to].Replace('\n', ' ').Replace('\r', ' ');
        return (from > 0 ? "…" : "") + slice.Trim() + (to < text.Length ? "…" : "");
    }

    /// <summary>Builds the projection from the verified ledger, staging drafts, and every active project folder.</summary>
    public static SearchIndex Build(IEnumerable<LedgerRecord> records, IDraftNoteStore drafts, ProjectRegistry registry, ICollection<string>? problems = null)
    {
        var index = new SearchIndex();
        foreach (var record in records) index.IndexCapture(record);
        foreach (var draft in drafts.Unrouted()) index.IndexDraft(draft);
        foreach (var project in registry.Active)
        {
            if (!Directory.Exists(project.RootPath)) { problems?.Add($"Project folder missing: {project.Slug} → {project.RootPath}"); continue; }
            var (notes, readProblems) = ProjectNoteStore.ReadAll(project.RootPath);
            foreach (var (note, _) in notes) index.IndexNote(note, project);
            foreach (var p in readProblems) problems?.Add($"{project.Slug}: {p.Path}: {p.Reason}");
        }
        return index;
    }
}
