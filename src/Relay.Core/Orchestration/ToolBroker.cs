using Relay.Core.Notes;
using Relay.Core.Preferences;
using Relay.Core.Projects;
using Relay.Core.Search;
using Relay.Core.Stream;

namespace Relay.Core.Orchestration;

public sealed record ToolDescriptor(string Name, string Description, IReadOnlyList<string> Arguments);

public sealed record ToolResult(bool Ok, string Summary, object? Data, string? Error, IReadOnlyList<SearchHit>? Hits = null)
{
    public static ToolResult Fail(string error) => new(false, "failed", null, error);
}

/// <summary>Everything the read-only tools may look at. Anything not here is not reachable from a plan.</summary>
public sealed class ToolSources
{
    public required ProjectRegistry Registry { get; init; }
    public required IDraftNoteStore Drafts { get; init; }
    public required SearchIndex Index { get; init; }
    public ExcerptStore? Excerpts { get; init; }
    /// <summary>Reads a stored external artifact by id; null when none is configured.</summary>
    public Func<string, string?>? ReadArtifact { get; init; }
    public Func<CompiledPreferences>? Preferences { get; init; }
}

/// <summary>
/// The read-only tools a planner may call (Tier A, contract §7.1). Every call and its outcome is
/// reported to the sink so the activity feed shows the reasoning trail while it happens. Calls beyond
/// the budget fail visibly instead of running. Tools return ids; only ids a tool returned may be cited.
/// </summary>
public sealed class ToolBroker
{
    private readonly ToolSources _sources;
    private readonly ITurnSink _sink;
    private readonly int _maxCalls;
    private int _calls;

    public ToolBroker(ToolSources sources, ITurnSink sink, int maxCalls)
    {
        _sources = sources;
        _sink = sink;
        _maxCalls = maxCalls;
    }

    public ToolBroker(ProjectRegistry registry, IDraftNoteStore drafts, SearchIndex index, ITurnSink sink, int maxCalls)
        : this(new ToolSources { Registry = registry, Drafts = drafts, Index = index }, sink, maxCalls) { }

    public static readonly IReadOnlyList<ToolDescriptor> Descriptors =
    [
        new("list_projects", "Active and archived projects with slug, name, aliases, and folder.", []),
        new("search", "Search selected conversation excerpts, draft notes, and project notes for words. Returns excerpts with source spans.", ["query", "project?", "limit?", "exclude?"]),
        new("read_note", "Read one canonical project note by id.", ["projectId", "noteId"]),
        new("project_notes", "List the notes of one project (id, type, status, first line).", ["projectId", "type?"]),
        new("list_draft_notes", "Draft notes still waiting in staging (the inbox).", []),
        new("read_excerpt", "Read a retained conversation excerpt by id (the words a task was triggered by).", ["excerptId"]),
        new("read_artifact", "Read a stored external response artifact by id.", ["artifactId"]),
        new("preferences", "The user's compiled preferences: response style, watched terms, standing grants, source permissions.", []),
    ];

    public int CallsMade => _calls;

    public ToolResult Call(string name, IReadOnlyDictionary<string, string> args)
    {
        if (_calls >= _maxCalls)
        {
            var over = ToolResult.Fail($"Tool budget of {_maxCalls} calls exhausted.");
            _sink.ToolCalled(name, args);
            _sink.ToolReturned(name, false, over.Error!, 0);
            return over;
        }
        _calls++;
        _sink.ToolCalled(name, args);
        ToolResult result;
        try
        {
            result = name switch
            {
                "list_projects" => ListProjects(),
                "search" => Search(args),
                "read_note" => ReadNote(args),
                "project_notes" => ProjectNotes(args),
                "list_draft_notes" => ListDrafts(),
                "read_excerpt" => ReadExcerpt(args),
                "read_artifact" => ReadArtifact(args),
                "preferences" => Preferences(),
                _ => ToolResult.Fail($"Unknown tool '{name}'. Tools: {string.Join(", ", Descriptors.Select(d => d.Name))}"),
            };
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or KeyNotFoundException)
        {
            result = ToolResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }
        _sink.ToolReturned(name, result.Ok, result.Ok ? result.Summary : result.Error!, result.Hits?.Count ?? (result.Data as System.Collections.ICollection)?.Count ?? (result.Ok ? 1 : 0));
        return result;
    }

    private ToolResult ListProjects()
    {
        var projects = _sources.Registry.All.Select(p => new { id = p.Id, slug = p.Slug, name = p.Name, aliases = p.Aliases, status = p.Status, path = p.RootPath }).ToList();
        return new ToolResult(true, $"{projects.Count} project(s)", projects, null);
    }

    private ToolResult Search(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("search needs 'query'.");
        string? projectId = null;
        if (args.TryGetValue("project", out var project) && !string.IsNullOrWhiteSpace(project))
        {
            var record = _sources.Registry.Find(project);
            if (record is null) return ToolResult.Fail($"No project matches '{project}'.");
            projectId = record.Id;
        }
        var limit = args.TryGetValue("limit", out var l) && int.TryParse(l, out var parsed) ? Math.Clamp(parsed, 1, 25) : 8;
        var hits = _sources.Index.Search(query, projectId, limit, excludeId: args.GetValueOrDefault("exclude"));
        var data = hits.Select(h => new { kind = h.Kind, id = h.Id, projectSlug = h.ProjectSlug, type = h.Type, status = h.Status, excerpt = h.Excerpt, span = h.Span, at = h.At }).ToList();
        return new ToolResult(true, $"{hits.Count} hit(s) for \"{query}\"", data, null, hits);
    }

    private ToolResult ReadNote(IReadOnlyDictionary<string, string> args)
    {
        var project = _sources.Registry.Find(args.GetValueOrDefault("projectId") ?? "") ?? throw new KeyNotFoundException("Unknown project.");
        var found = ProjectNoteStore.Find(project.RootPath, args.GetValueOrDefault("noteId") ?? "");
        if (found is null) return ToolResult.Fail("Note not found.");
        var n = found.Value.Note;
        var hit = new SearchHit(SearchIndex.NoteKind, n.Id, project.Id, project.Slug, n.Type, FirstLine(n.Body), 1, n.Spans.FirstOrDefault(), n.Created, n.Body, n.Status);
        return new ToolResult(true, $"note {n.Id} ({n.Type}, {n.Status})", new { id = n.Id, type = n.Type, status = n.Status, created = n.Created, confidence = n.Confidence, topic = n.Topic, spans = n.Spans, supersedes = n.Supersedes, body = n.Body }, null, [hit]);
    }

    private ToolResult ProjectNotes(IReadOnlyDictionary<string, string> args)
    {
        var project = _sources.Registry.Find(args.GetValueOrDefault("projectId") ?? "") ?? throw new KeyNotFoundException("Unknown project.");
        if (!Directory.Exists(project.RootPath)) return ToolResult.Fail($"Project folder is missing: {project.RootPath}");
        var (notes, problems) = ProjectNoteStore.ReadAll(project.RootPath);
        var type = args.GetValueOrDefault("type");
        var selected = notes.Where(n => type is null || n.Note.Type == type).ToList();
        var list = selected.Select(n => new { id = n.Note.Id, type = n.Note.Type, status = n.Note.Status, created = n.Note.Created, firstLine = FirstLine(n.Note.Body) }).ToList();
        var hits = selected.Select(n => new SearchHit(SearchIndex.NoteKind, n.Note.Id, project.Id, project.Slug, n.Note.Type, FirstLine(n.Note.Body), 1, n.Note.Spans.FirstOrDefault(), n.Note.Created, n.Note.Body, n.Note.Status)).ToList();
        var summary = $"{list.Count} note(s) in {project.Slug}" + (problems.Count > 0 ? $", {problems.Count} unreadable" : "");
        return new ToolResult(true, summary, list, null, hits);
    }

    private ToolResult ListDrafts()
    {
        var drafts = _sources.Drafts.Unrouted().Select(d => new { id = d.NoteId, type = d.Type, created = d.CreatedAt, chars = d.Text.Length, firstLine = FirstLine(d.Text) }).ToList();
        return new ToolResult(true, $"{drafts.Count} draft note(s) in staging", drafts, null);
    }

    private ToolResult ReadExcerpt(IReadOnlyDictionary<string, string> args)
    {
        if (_sources.Excerpts is null) return ToolResult.Fail("No excerpt store is configured.");
        var excerpt = _sources.Excerpts.Read(args.GetValueOrDefault("excerptId") ?? "");
        if (excerpt is null) return ToolResult.Fail("Excerpt not found.");
        var hit = new SearchHit(SearchIndex.ExcerptKind, excerpt.ExcerptId, null, null, "excerpt", FirstLine(excerpt.Text), 1, new SourceSpan(excerpt.ExcerptId, 0, excerpt.Text.Length), excerpt.From, excerpt.Text);
        return new ToolResult(true, $"excerpt {excerpt.ExcerptId} ({excerpt.Segments.Count} segment(s), {excerpt.Seconds:0}s)",
            new { id = excerpt.ExcerptId, from = excerpt.From, to = excerpt.To, reason = excerpt.Reason, segments = excerpt.Segments.Select(s => new { s.SegmentId, s.At, s.Speaker, s.Text }), references = excerpt.References }, null, [hit]);
    }

    private ToolResult ReadArtifact(IReadOnlyDictionary<string, string> args)
    {
        if (_sources.ReadArtifact is null) return ToolResult.Fail("No artifact store is configured.");
        var id = args.GetValueOrDefault("artifactId") ?? "";
        var text = _sources.ReadArtifact(id);
        if (text is null) return ToolResult.Fail("Artifact not found.");
        var hit = new SearchHit(SearchIndex.ArtifactKind, id, null, null, "artifact", FirstLine(text), 1, null, DateTimeOffset.MinValue, text);
        return new ToolResult(true, $"artifact {id} ({text.Length} chars)", new { id, chars = text.Length, text = text.Length > 12_000 ? text[..12_000] + "…(truncated)" : text }, null, [hit]);
    }

    private ToolResult Preferences()
    {
        if (_sources.Preferences is null) return ToolResult.Fail("Preferences are not available to this planner.");
        var p = _sources.Preferences();
        return new ToolResult(true, "preferences", new
        {
            responseStyle = p.PromptFragment,
            maxAnswerChars = p.MaxAnswerChars,
            watchedTerms = p.WatchedTerms,
            grants = p.Grants.Select(g => new { g.GrantId, g.Action, g.ProjectId, g.NoteType }),
            allowOnlineSearch = p.AllowOnlineSearch,
            bufferSeconds = p.Buffer.TotalSeconds,
        }, null);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 120 ? line[..117] + "…" : line;
    }
}
