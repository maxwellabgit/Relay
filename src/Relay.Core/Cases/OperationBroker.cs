using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Cases;

public sealed record OperationApplyResult(bool Ok, string Summary, string? ResultRef = null, string? Error = null);

/// <summary>
/// Applies approved operation envelopes for project/note writes using existing stores.
/// Slice-1 side-effect capability remains a counter hook.
/// </summary>
public sealed class OperationBroker
{
    private readonly CaseLocalContext _local;
    private readonly ObjectStore _objects;
    private readonly IClock _clock;
    private readonly Action _onSideEffect;

    public OperationBroker(CaseLocalContext local, ObjectStore objects, IClock clock, Action? onSideEffect = null)
    {
        _local = local;
        _objects = objects;
        _clock = clock;
        _onSideEffect = onSideEffect ?? (() => { });
    }

    public OperationApplyResult Apply(OperationEnvelope envelope)
    {
        try
        {
            return envelope.Capability switch
            {
                ScriptedCaseMind.DefaultCapability => ApplySideEffect(envelope),
                Actions.CreateProject => ApplyCreateProject(envelope),
                Actions.ModifyNote => ApplyModifyNote(envelope),
                Actions.CreateDraftNote => ApplyCreateDraftNote(envelope),
                "file_note" => ApplyFileNote(envelope),
                _ => new OperationApplyResult(false, "Unknown capability.", Error: $"Unknown capability '{envelope.Capability}'."),
            };
        }
        catch (Exception ex)
        {
            return new OperationApplyResult(false, ex.Message, Error: ex.Message);
        }
    }

    private OperationApplyResult ApplySideEffect(OperationEnvelope envelope)
    {
        _onSideEffect();
        var stored = _objects.PutJson(new { ok = true, capability = envelope.Capability, at = _clock.UtcNow });
        return new OperationApplyResult(true, "Side effect applied.", stored.ObjectId);
    }

    private OperationApplyResult ApplyCreateProject(OperationEnvelope envelope)
    {
        var name = ReqString(envelope, "name");
        var slug = OptString(envelope, "slug") ?? Slug.From(name);
        var now = _clock.UtcNow;
        var id = Ulid.NewUlid(now);
        var root = Path.Combine(_local.ProjectsHome, slug);
        var record = new ProjectRecord
        {
            Id = id,
            Slug = slug,
            Name = name,
            RootPath = root,
            CreatedAt = now,
        };
        Directory.CreateDirectory(_local.ProjectsHome);
        ProjectLayout.Create(record, now);
        _local.Registry.Add(record);
        _local.RebuildIndex();
        _onSideEffect();
        var stored = _objects.PutJson(new { projectId = id, slug, name, path = root });
        return new OperationApplyResult(true, $"Created project '{name}'.", stored.ObjectId);
    }

    private OperationApplyResult ApplyModifyNote(OperationEnvelope envelope)
    {
        var projectId = ReqString(envelope, "projectId");
        var noteId = ReqString(envelope, "noteId");
        var project = _local.Registry.ById(projectId) ?? throw new InvalidOperationException("Project not found.");
        var found = ProjectNoteStore.Find(project.RootPath, noteId) ?? throw new FileNotFoundException("Note not found.");
        var note = found.Note;
        if (OptString(envelope, "body") is { } body && body.Length > 0) note.Body = body;
        if (OptString(envelope, "status") is { } status) note.Status = status;
        if (OptString(envelope, "type") is { } type && NoteTypes.All.Contains(type)) note.Type = type;
        if (OptString(envelope, "topic") is { } topic) note.Topic = topic;
        var written = ProjectNoteStore.WriteVersion(project.RootPath, note);
        _local.RebuildIndex();
        _onSideEffect();
        var stored = _objects.PutJson(new { noteId, projectId, path = written.Path, sha256 = written.Sha256, version = written.Version });
        return new OperationApplyResult(true, $"Updated note {noteId}.", stored.ObjectId);
    }

    private OperationApplyResult ApplyCreateDraftNote(OperationEnvelope envelope)
    {
        var text = ReqString(envelope, "text");
        var type = OptString(envelope, "type") ?? NoteTypes.Decision;
        var now = _clock.UtcNow;
        var noteId = Ulid.NewUlid(now);
        var draft = new DraftNote(
            noteId,
            CaptureId: OptString(envelope, "captureId") ?? noteId,
            SourceEventId: OptString(envelope, "sourceEventId") ?? noteId,
            CreatedAt: now,
            Type: type,
            Status: DraftNote.DraftStatus,
            ProjectId: OptString(envelope, "projectId"),
            Routing: DraftNote.UnroutedRouting,
            Confidence: null,
            Text: text,
            Spans: [],
            Topic: OptString(envelope, "topic"));
        var path = Path.Combine(_local.Root.DraftNotesDirectory, noteId + ".json");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(draft, RelayJson.Indented));
        _onSideEffect();
        var stored = _objects.PutJson(new { noteId, path, type });
        return new OperationApplyResult(true, $"Draft note {noteId} stored.", stored.ObjectId);
    }

    private OperationApplyResult ApplyFileNote(OperationEnvelope envelope)
    {
        var projectId = ReqString(envelope, "projectId");
        var text = ReqString(envelope, "text");
        var type = OptString(envelope, "type") ?? NoteTypes.Decision;
        var project = _local.Registry.ById(projectId) ?? throw new InvalidOperationException("Project not found.");
        var now = _clock.UtcNow;
        var note = new NoteDocument
        {
            Id = Ulid.NewUlid(now),
            ProjectId = project.Id,
            Type = type,
            Status = NoteStatus.Active,
            Created = now,
            Topic = OptString(envelope, "topic"),
            Body = text,
        };
        var written = ProjectNoteStore.WriteNew(project.RootPath, note);
        _local.RebuildIndex();
        _onSideEffect();
        var stored = _objects.PutJson(new { noteId = note.Id, projectId, path = written.Path, sha256 = written.Sha256 });
        return new OperationApplyResult(true, $"Filed note {note.Id}.", stored.ObjectId);
    }

    private static string ReqString(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
            throw new InvalidOperationException($"Missing argument '{key}'.");
        return el.GetString()!;
    }

    private static string? OptString(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String) return null;
        return el.GetString();
    }
}
