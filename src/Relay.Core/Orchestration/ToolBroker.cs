using Relay.Core.Notes;
using Relay.Core.Projects;
using Relay.Core.Search;

namespace Relay.Core.Orchestration;

public sealed record ToolDescriptor(string Name, string Description, IReadOnlyList<string> Arguments);

public sealed record ToolResult(bool Ok, string Summary, object? Data, string? Error, IReadOnlyList<SearchHit>? Hits = null)
{
    public static ToolResult Fail(string error) => new(false, "failed", null, error);
}

/// <summary>
/// The read-only tools an orchestrator may call during planning (Tier A, contract §7.1). Every
/// call and its outcome is reported to the sink so the activity feed shows the reasoning trail
/// while it happens. Calls beyond the budget fail visibly instead of running.
/// </summary>
public sealed class ToolBroker
{
    private readonly ProjectRegistry _registry;
    private readonly IDraftNoteStore _drafts;
    private readonly SearchIndex _index;
    private readonly ITurnSink _sink;
    private readonly int _maxCalls;
    private int _calls;

    public ToolBroker(ProjectRegistry registry, IDraftNoteStore drafts, SearchIndex index, ITurnSink sink, int maxCalls)
    {
        _registry = registry;
        _drafts = drafts;
        _index = index;
        _sink = sink;
        _maxCalls = maxCalls;
    }

    public static readonly IReadOnlyList<ToolDescriptor> Descriptors =
    [
        new("list_projects", "Active and archived projects with slug, name, aliases, and folder.", []),
        new("search", "Search stored captures, draft notes, and project notes for words. Returns excerpts with ledger spans.", ["query", "project?", "limit?", "exclude?"]),
        new("read_note", "Read one canonical project note by id.", ["projectId", "noteId"]),
        new("project_notes", "List the notes of one project (id, type, status, first line).", ["projectId", "type?"]),
        new("list_draft_notes", "Draft notes still waiting in staging.", []),
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
        var projects = _registry.All.Select(p => new { id = p.Id, slug = p.Slug, name = p.Name, aliases = p.Aliases, status = p.Status, path = p.RootPath }).ToList();
        return new ToolResult(true, $"{projects.Count} project(s)", projects, null);
    }

    private ToolResult Search(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("search needs 'query'.");
        string? projectId = null;
        if (args.TryGetValue("project", out var project) && !string.IsNullOrWhiteSpace(project))
        {
            var record = _registry.Find(project);
            if (record is null) return ToolResult.Fail($"No project matches '{project}'.");
            projectId = record.Id;
        }
        var limit = args.TryGetValue("limit", out var l) && int.TryParse(l, out var parsed) ? Math.Clamp(parsed, 1, 25) : 8;
        var hits = _index.Search(query, projectId, limit, excludeId: args.GetValueOrDefault("exclude"));
        var data = hits.Select(h => new { kind = h.Kind, id = h.Id, projectSlug = h.ProjectSlug, type = h.Type, excerpt = h.Excerpt, span = h.Span, at = h.At }).ToList();
        return new ToolResult(true, $"{hits.Count} hit(s) for \"{query}\"", data, null, hits);
    }

    private ToolResult ReadNote(IReadOnlyDictionary<string, string> args)
    {
        var project = _registry.Find(args.GetValueOrDefault("projectId") ?? "") ?? throw new KeyNotFoundException("Unknown project.");
        var found = ProjectNoteStore.Find(project.RootPath, args.GetValueOrDefault("noteId") ?? "");
        if (found is null) return ToolResult.Fail("Note not found.");
        var n = found.Value.Note;
        return new ToolResult(true, $"note {n.Id} ({n.Type}, {n.Status})", new { id = n.Id, type = n.Type, status = n.Status, created = n.Created, confidence = n.Confidence, topic = n.Topic, spans = n.Spans, supersedes = n.Supersedes, body = n.Body }, null);
    }

    private ToolResult ProjectNotes(IReadOnlyDictionary<string, string> args)
    {
        var project = _registry.Find(args.GetValueOrDefault("projectId") ?? "") ?? throw new KeyNotFoundException("Unknown project.");
        if (!Directory.Exists(project.RootPath)) return ToolResult.Fail($"Project folder is missing: {project.RootPath}");
        var (notes, problems) = ProjectNoteStore.ReadAll(project.RootPath);
        var type = args.GetValueOrDefault("type");
        var list = notes.Where(n => type is null || n.Note.Type == type)
            .Select(n => new { id = n.Note.Id, type = n.Note.Type, status = n.Note.Status, created = n.Note.Created, firstLine = FirstLine(n.Note.Body) }).ToList();
        var summary = $"{list.Count} note(s) in {project.Slug}" + (problems.Count > 0 ? $", {problems.Count} unreadable" : "");
        return new ToolResult(true, summary, list, null);
    }

    private ToolResult ListDrafts()
    {
        var drafts = _drafts.Unrouted().Select(d => new { id = d.NoteId, type = d.Type, created = d.CreatedAt, chars = d.Text.Length, firstLine = FirstLine(d.Text) }).ToList();
        return new ToolResult(true, $"{drafts.Count} draft note(s) in staging", drafts, null);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 120 ? line[..117] + "…" : line;
    }
}
