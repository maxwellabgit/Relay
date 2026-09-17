using System.Text.Json;
using Relay.Core.Notes;
using Relay.Core.Search;

namespace Relay.Core.Cases;

/// <summary>Deterministic local tools available to <see cref="CaseRuntime"/> for Slice 2.</summary>
public static class CaseTools
{
    public const string LocalSearch = "local_search";
    public const string ReadNote = "read_note";

    public static object LocalSearchResult(CaseLocalContext local, string query, string? projectId, int limit = 8)
    {
        var hits = local.Index.Search(query, projectId, limit, kinds: [SearchIndex.NoteKind]);
        return new
        {
            query,
            hits = hits.Select(h => new
            {
                kind = h.Kind,
                id = h.Id,
                projectId = h.ProjectId,
                projectSlug = h.ProjectSlug,
                type = h.Type,
                excerpt = h.Excerpt,
                score = h.Score,
                status = h.Status,
                span = h.Span is null ? null : new { eventId = h.Span.EventId, start = h.Span.Start, end = h.Span.End },
            }).ToList(),
        };
    }

    public static object ReadNoteResult(CaseLocalContext local, string projectId, string noteId)
    {
        var found = local.FindNote(projectId, noteId);
        if (found is null)
            throw new FileNotFoundException($"Note '{noteId}' not found in project '{projectId}'.");
        var note = found.Value.Note;
        var span = note.Spans.FirstOrDefault();
        return new
        {
            noteId = note.Id,
            projectId = note.ProjectId,
            type = note.Type,
            status = note.Status,
            topic = note.Topic,
            body = note.Body,
            sourceEventId = span?.EventId,
            spanStart = span?.Start ?? 0,
            spanEnd = span?.End ?? note.Body.Length,
            path = found.Value.Path,
        };
    }

    public static string ArgString(IReadOnlyDictionary<string, JsonElement> args, string key, string? fallback = null)
    {
        if (args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? fallback ?? "";
        return fallback ?? "";
    }
}
