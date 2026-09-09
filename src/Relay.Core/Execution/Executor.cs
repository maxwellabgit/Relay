using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Backup;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Storage;
using Relay.Core.Time;
using Relay.Core.Workspaces;

namespace Relay.Core.Execution;

public enum ExecutionStatus { Completed, Failed, Pending }

public sealed record ExecutionResult(ExecutionStatus Status, string Summary, IReadOnlyDictionary<string, string> Outputs, string? Error)
{
    public static ExecutionResult Ok(string summary, IReadOnlyDictionary<string, string>? outputs = null) => new(ExecutionStatus.Completed, summary, outputs ?? new Dictionary<string, string>(), null);
    public static ExecutionResult Fail(string error) => new(ExecutionStatus.Failed, "Failed", new Dictionary<string, string>(), error);
    public static ExecutionResult Pending(string summary, IReadOnlyDictionary<string, string> outputs) => new(ExecutionStatus.Pending, summary, outputs, null);
}

/// <summary>The executor records through the coordinator so every event lands in the single ledger writer.</summary>
public interface IExecutionSink
{
    LedgerRecord? Record(string type, object data);
}

/// <summary>Worker operations arrive in phase 5; the executor only knows the contract.</summary>
public interface IWorkerOperations
{
    ExecutionResult Launch(Proposal proposal, Decision decision, string turnId, IExecutionSink sink);
    ExecutionResult ApplyPatch(Proposal proposal, Decision decision, IExecutionSink sink);
}

/// <summary>An approved package leaves the machine for a named external model; the runtime resolves references, hashes the package, sends, and stores the artifact.</summary>
public interface IExternalOperations
{
    ExecutionResult Request(Proposal proposal, Decision decision, string taskId, IExecutionSink sink);
}

/// <summary>Relay's own configuration changes go through change sets; the executor only knows the contract.</summary>
public interface ISelfChangeOperations
{
    ExecutionResult UpdatePreference(Proposal proposal, Decision decision, string taskId, IExecutionSink sink);
    ExecutionResult UpdatePrompt(Proposal proposal, Decision decision, string taskId, IExecutionSink sink);
}

/// <summary>A tested draft tool Relay built becomes a promoted tool as one change set (docs/09, slice 6); the executor only knows the contract.</summary>
public interface IToolOperations
{
    ExecutionResult Promote(Proposal proposal, Decision decision, string taskId, IExecutionSink sink);
}

public sealed class ExecutionJournalEntry
{
    [JsonPropertyName("proposalId")] public required string ProposalId { get; init; }
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("turnId")] public required string TurnId { get; init; }
    [JsonPropertyName("startedAt")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("target")] public IReadOnlyDictionary<string, string>? Target { get; init; }
}

/// <summary>
/// The only component that performs controlled writes. Runs one proposal at a time, only with a
/// valid single-use capability, only after re-checking policy against the current world, and
/// journals start/finish so a crash mid-operation is detected at the next start (contract §8.3).
/// </summary>
public sealed class Executor
{
    private readonly DataRoot _root;
    private readonly ProjectRegistry _registry;
    private readonly WorkspaceRoots _roots;
    private readonly IDraftNoteStore _drafts;
    private readonly IClock _clock;
    private readonly CapabilityIssuer _capabilities;

    public Executor(DataRoot root, ProjectRegistry registry, WorkspaceRoots roots, IDraftNoteStore drafts, IClock clock, CapabilityIssuer capabilities)
    {
        _root = root;
        _registry = registry;
        _roots = roots;
        _drafts = drafts;
        _clock = clock;
        _capabilities = capabilities;
    }

    public IWorkerOperations? Workers { get; set; }
    public IExternalOperations? External { get; set; }
    public ISelfChangeOperations? SelfChange { get; set; }
    public IToolOperations? Tools { get; set; }

    /// <param name="overheard">The task began from something overheard: the target's prose (note text, an objective) is fingerprinted in the ledger; the journal keeps it.</param>
    public ExecutionResult Execute(Proposal proposal, Capability capability, PolicyWorld world, string turnId, IExecutionSink sink, bool overheard = false)
    {
        var check = _capabilities.Consume(capability, proposal, _clock.UtcNow);
        if (!check.Ok)
        {
            sink.Record(EventTypes.ExecutionFailed, new { proposalId = proposal.ProposalId, action = proposal.Action, turnId, error = check.Reason, stage = "capability" });
            return ExecutionResult.Fail(check.Reason!);
        }

        // Policy is re-evaluated at execution time: the world may have changed since approval.
        var decision = PolicyEngine.Decide(proposal, world);
        if (decision.Outcome == DecisionOutcome.Deny)
        {
            var error = "Preconditions no longer hold: " + string.Join(" ", decision.Reasons);
            sink.Record(EventTypes.ExecutionFailed, new { proposalId = proposal.ProposalId, action = proposal.Action, turnId, error, stage = "recheck" });
            return ExecutionResult.Fail(error);
        }

        var journal = new ExecutionJournalEntry { ProposalId = proposal.ProposalId, Action = proposal.Action, TurnId = turnId, StartedAt = _clock.UtcNow, Target = decision.NormalizedTarget };
        WriteJournal(journal);
        sink.Record(EventTypes.ExecutionStarted, new { proposalId = proposal.ProposalId, action = proposal.Action, turnId, target = overheard ? Withheld.Target(decision.NormalizedTarget) : decision.NormalizedTarget });

        ExecutionResult result;
        try
        {
            result = proposal.Action switch
            {
                Actions.CreateDraftNote => CreateDraftNote(proposal, decision, sink),
                Actions.RouteNote => RouteNote(proposal, decision, sink),
                Actions.CreateProject => CreateProject(proposal, decision, sink),
                Actions.ModifyNote => ModifyNote(proposal, decision, sink),
                Actions.SupersedeNote => SupersedeNote(proposal, decision, sink),
                Actions.RenameProject => RenameProject(proposal, decision, sink),
                Actions.ArchiveProject => ArchiveProject(proposal, decision, sink),
                Actions.RestoreProject => RestoreProject(proposal, decision, sink),
                Actions.ExportBackup => ExportBackup(proposal, decision, sink),
                Actions.LaunchWorker => Workers?.Launch(proposal, decision, turnId, sink) ?? ExecutionResult.Fail("Worker runtime is not configured."),
                Actions.ApplyPatch => Workers?.ApplyPatch(proposal, decision, sink) ?? ExecutionResult.Fail("Worker runtime is not configured."),
                Actions.MoveNote => MoveNote(proposal, decision, sink),
                Actions.DeleteProject => DeleteProject(proposal, decision, sink),
                Actions.ModelRequest => External?.Request(proposal, decision, turnId, sink) ?? ExecutionResult.Fail("No external model runtime is configured."),
                Actions.UpdatePreference => SelfChange?.UpdatePreference(proposal, decision, turnId, sink) ?? ExecutionResult.Fail("Self-change operations are not configured."),
                Actions.UpdatePrompt => SelfChange?.UpdatePrompt(proposal, decision, turnId, sink) ?? ExecutionResult.Fail("Self-change operations are not configured."),
                Actions.AddTool => Tools?.Promote(proposal, decision, turnId, sink) ?? ExecutionResult.Fail("Tool building is not configured."),
                _ => ExecutionResult.Fail($"No executor for action '{proposal.Action}'."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or JsonException)
        {
            result = ExecutionResult.Fail(ex.GetType().Name + ": " + ex.Message);
        }

        if (result.Status == ExecutionStatus.Pending)
        {
            return result; // the owner completes the journal when the asynchronous work finishes
        }
        Complete(proposal, turnId, result, sink);
        return result;
    }

    /// <summary>Finishes a pending execution (worker runs) once the asynchronous part has reported.</summary>
    public void Complete(Proposal proposal, string turnId, ExecutionResult result, IExecutionSink sink)
    {
        var journal = ReadJournal(proposal.ProposalId) ?? new ExecutionJournalEntry { ProposalId = proposal.ProposalId, Action = proposal.Action, TurnId = turnId, StartedAt = _clock.UtcNow };
        journal.CompletedAt = _clock.UtcNow;
        journal.Status = result.Status.ToString().ToLowerInvariant();
        journal.Error = result.Error;
        WriteJournal(journal);
        if (result.Status == ExecutionStatus.Completed)
            sink.Record(EventTypes.ExecutionCompleted, new { proposalId = proposal.ProposalId, action = proposal.Action, turnId, summary = result.Summary, outputs = result.Outputs });
        else
            sink.Record(EventTypes.ExecutionFailed, new { proposalId = proposal.ProposalId, action = proposal.Action, turnId, error = result.Error, stage = "operation" });
    }

    // ----------------------------------------------------------------------------------------
    // Operations
    // ----------------------------------------------------------------------------------------

    private ExecutionResult CreateDraftNote(Proposal p, Decision d, IExecutionSink sink)
    {
        var text = d.NormalizedTarget["text"];
        var now = _clock.UtcNow;
        var sourceEventId = p.SourceEventIds.FirstOrDefault() ?? d.NormalizedTarget.GetValueOrDefault("sourceEventId") ?? "";
        var spanStart = int.TryParse(d.NormalizedTarget.GetValueOrDefault("spanStart"), out var ss) && ss >= 0 ? ss : 0;
        var spanEnd = int.TryParse(d.NormalizedTarget.GetValueOrDefault("spanEnd"), out var se) && se > spanStart ? se : spanStart + text.Length;
        var note = new DraftNote(Ulid.NewUlid(now), d.NormalizedTarget.GetValueOrDefault("captureId") ?? "", sourceEventId, now,
            d.NormalizedTarget.GetValueOrDefault("type", NoteTypes.Idea), DraftNote.DraftStatus, null, DraftNote.UnroutedRouting, null,
            text, [new SourceSpan(sourceEventId, spanStart, spanEnd)]);
        var path = _drafts.Write(note);
        sink.Record(EventTypes.NoteDraftCreated, new { noteId = note.NoteId, captureId = note.CaptureId, sourceEventId, chars = text.Length, path, proposalId = p.ProposalId });
        return ExecutionResult.Ok($"Saved draft note {note.NoteId}", new Dictionary<string, string> { ["noteId"] = note.NoteId, ["path"] = path });
    }

    private ExecutionResult RouteNote(Proposal p, Decision d, IExecutionSink sink)
    {
        var draft = _drafts.Read(d.NormalizedTarget["noteId"]) ?? throw new FileNotFoundException("Draft note vanished.");
        var project = _registry.ById(d.NormalizedTarget["projectId"]) ?? throw new InvalidOperationException("Project vanished.");
        double? confidence = double.TryParse(d.NormalizedTarget.GetValueOrDefault("confidence"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : null;
        var type = d.NormalizedTarget.GetValueOrDefault("type") ?? (draft.Type == DraftNote.RawCaptureType ? NoteTypes.Idea : draft.Type);
        var disputedWith = (d.NormalizedTarget.GetValueOrDefault("disputedWith") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var note = new NoteDocument
        {
            Id = draft.NoteId,
            ProjectId = project.Id,
            Type = type,
            Status = disputedWith.Count > 0 ? NoteStatus.Disputed : d.NormalizedTarget.GetValueOrDefault("status") ?? NoteStatus.Active,
            Created = draft.CreatedAt,
            Confidence = confidence,
            CaptureId = draft.CaptureId,
            Topic = d.NormalizedTarget.GetValueOrDefault("topic") ?? draft.Topic ?? Relay.Core.Memory.NoteExtractor.Topic(draft.Text),
            Spans = draft.Spans.ToList(),
            DisputedWith = disputedWith,
            Body = draft.Text,
        };
        var written = ProjectNoteStore.WriteNew(project.RootPath, note);
        var stagingPath = _drafts.MarkRouted(draft.NoteId, project.Id, confidence);
        sink.Record(EventTypes.NoteRouted, new { noteId = note.Id, projectId = project.Id, projectSlug = project.Slug, path = written.Path, confidence, by = p.ProposedBy, proposalId = p.ProposalId, stagingPath, status = note.Status, type });
        sink.Record(EventTypes.NoteWritten, new { noteId = note.Id, projectId = project.Id, path = written.Path, sha256 = written.Sha256, version = written.Version, type });
        return ExecutionResult.Ok($"Filed note {note.Id} under {project.Slug}/{NoteTypes.Folder(type)}", new Dictionary<string, string> { ["path"] = written.Path, ["sha256"] = written.Sha256, ["projectId"] = project.Id });
    }

    private ExecutionResult CreateProject(Proposal p, Decision d, IExecutionSink sink)
    {
        var now = _clock.UtcNow;
        var record = new ProjectRecord
        {
            Id = Ulid.NewUlid(now),
            Slug = d.NormalizedTarget["slug"],
            Name = d.NormalizedTarget["name"],
            RootPath = d.NormalizedTarget["path"],
            CreatedAt = now,
        };
        ProjectLayout.Create(record, now);
        _registry.Add(record);
        sink.Record(EventTypes.ProjectCreated, new { projectId = record.Id, slug = record.Slug, name = record.Name, path = record.RootPath, proposalId = p.ProposalId, by = p.ProposedBy });
        return ExecutionResult.Ok($"Created project '{record.Name}' ({record.Slug}) at {record.RootPath}", new Dictionary<string, string> { ["projectId"] = record.Id, ["path"] = record.RootPath, ["slug"] = record.Slug });
    }

    private ExecutionResult ModifyNote(Proposal p, Decision d, IExecutionSink sink)
    {
        var project = _registry.ById(d.NormalizedTarget["projectId"])!;
        var found = ProjectNoteStore.Find(project.RootPath, d.NormalizedTarget["noteId"]) ?? throw new FileNotFoundException("Note vanished.");
        var note = found.Note;
        if (d.NormalizedTarget.TryGetValue("body", out var body) && !string.IsNullOrWhiteSpace(body)) note.Body = body;
        if (d.NormalizedTarget.TryGetValue("status", out var status))
        {
            note.Status = status;
            if (status == NoteStatus.Active) note.DisputedWith.Clear(); // "keep both" resolves the dispute link
        }
        if (d.NormalizedTarget.TryGetValue("type", out var type) && NoteTypes.All.Contains(type)) note.Type = type;
        if (d.NormalizedTarget.TryGetValue("topic", out var topic)) note.Topic = topic;
        var written = ProjectNoteStore.WriteVersion(project.RootPath, note);
        sink.Record(EventTypes.NoteModified, new { noteId = note.Id, projectId = project.Id, path = written.Path, fromHash = written.PreviousSha256, toHash = written.Sha256, version = written.Version, versionPath = written.PreviousVersionPath, proposalId = p.ProposalId });
        return ExecutionResult.Ok($"Updated note {note.Id} (version {written.Version}); previous text kept at {written.PreviousVersionPath}", new Dictionary<string, string> { ["path"] = written.Path, ["sha256"] = written.Sha256 });
    }

    private ExecutionResult SupersedeNote(Proposal p, Decision d, IExecutionSink sink)
    {
        var project = _registry.ById(d.NormalizedTarget["projectId"])!;
        var old = ProjectNoteStore.Find(project.RootPath, d.NormalizedTarget["noteId"]) ?? throw new FileNotFoundException("Note vanished.");
        (NoteDocument Note, string Path) replacement;
        var created = false;
        if (d.NormalizedTarget.TryGetValue("newText", out var newText) && !string.IsNullOrWhiteSpace(newText))
        {
            // The replacement is written here, in the same journaled operation: one approval updates the record.
            var now = _clock.UtcNow;
            var spans = new List<SourceSpan>();
            if (d.NormalizedTarget.TryGetValue("sourceExcerptId", out var excerptId) && !string.IsNullOrWhiteSpace(excerptId))
            {
                var start = int.TryParse(d.NormalizedTarget.GetValueOrDefault("spanStart"), out var ss) && ss >= 0 ? ss : 0;
                var end = int.TryParse(d.NormalizedTarget.GetValueOrDefault("spanEnd"), out var se) && se > start ? se : start;
                spans.Add(new SourceSpan(excerptId, start, end));
            }
            var fresh = new NoteDocument
            {
                Id = Ulid.NewUlid(now),
                ProjectId = project.Id,
                Type = d.NormalizedTarget.TryGetValue("type", out var type) && NoteTypes.All.Contains(type) ? type : old.Note.Type,
                Status = NoteStatus.Active,
                Created = now,
                CaptureId = d.NormalizedTarget.GetValueOrDefault("captureId"),
                Topic = d.NormalizedTarget.GetValueOrDefault("topic") ?? old.Note.Topic,
                Spans = spans,
                Body = newText,
            };
            var written = ProjectNoteStore.WriteNew(project.RootPath, fresh);
            sink.Record(EventTypes.NoteWritten, new { noteId = fresh.Id, projectId = project.Id, path = written.Path, sha256 = written.Sha256, version = written.Version, type = fresh.Type, replaces = old.Note.Id, proposalId = p.ProposalId });
            replacement = (fresh, written.Path);
            created = true;
        }
        else
        {
            replacement = ProjectNoteStore.Find(project.RootPath, d.NormalizedTarget["supersededBy"]) ?? throw new FileNotFoundException("Replacement note vanished.");
        }
        old.Note.Status = NoteStatus.Superseded;
        old.Note.DisputedWith.Remove(replacement.Note.Id);
        if (!replacement.Note.Supersedes.Contains(old.Note.Id)) replacement.Note.Supersedes.Add(old.Note.Id);
        replacement.Note.DisputedWith.Remove(old.Note.Id);
        if (replacement.Note.Status == NoteStatus.Disputed && replacement.Note.DisputedWith.Count == 0) replacement.Note.Status = NoteStatus.Active;
        var w1 = ProjectNoteStore.WriteVersion(project.RootPath, old.Note);
        var w2 = ProjectNoteStore.WriteVersion(project.RootPath, replacement.Note);
        sink.Record(EventTypes.NoteSuperseded, new { oldNoteId = old.Note.Id, newNoteId = replacement.Note.Id, projectId = project.Id, oldVersionPath = w1.PreviousVersionPath, newVersionPath = w2.PreviousVersionPath, createdReplacement = created, proposalId = p.ProposalId });
        var outputs = new Dictionary<string, string> { ["oldPath"] = w1.Path, ["newPath"] = w2.Path, ["newNoteId"] = replacement.Note.Id, ["projectId"] = project.Id };
        return ExecutionResult.Ok(created
            ? $"Recorded the new {replacement.Note.Type} as {replacement.Note.Id} and marked {old.Note.Id} superseded; the earlier text is kept"
            : $"Marked {old.Note.Id} superseded by {replacement.Note.Id}; both earlier versions kept", outputs);
    }

    private ExecutionResult RenameProject(Proposal p, Decision d, IExecutionSink sink)
    {
        var project = _registry.ById(d.NormalizedTarget["projectId"])!;
        var oldSlug = project.Slug;
        var oldPath = project.RootPath;
        var oldName = project.Name;
        if (d.NormalizedTarget.TryGetValue("newName", out var newName) && !string.IsNullOrWhiteSpace(newName)) project.Name = newName;
        if (d.NormalizedTarget.TryGetValue("newSlug", out var newSlug) && !string.IsNullOrWhiteSpace(newSlug)) project.Slug = newSlug;
        if (d.NormalizedTarget.TryGetValue("newPath", out var newPath) && !string.IsNullOrWhiteSpace(newPath))
        {
            Directory.Move(oldPath, newPath);
            project.RootPath = newPath;
        }
        if (!project.Aliases.Contains(oldName, StringComparer.OrdinalIgnoreCase) && !string.Equals(oldName, project.Name, StringComparison.OrdinalIgnoreCase)) project.Aliases.Add(oldName);
        AtomicFile.WriteAllText(ProjectLayout.ProjectToml(project.RootPath), ProjectLayout.RenderToml(project, _clock.UtcNow));
        _registry.Update(project);
        sink.Record(EventTypes.ProjectRenamed, new { projectId = project.Id, oldSlug, newSlug = project.Slug, oldName, newName = project.Name, oldPath, newPath = project.RootPath, proposalId = p.ProposalId });
        return ExecutionResult.Ok($"Renamed project {oldSlug} → {project.Slug} ('{project.Name}')", new Dictionary<string, string> { ["path"] = project.RootPath });
    }

    private ExecutionResult ArchiveProject(Proposal p, Decision d, IExecutionSink sink)
    {
        var project = _registry.ById(d.NormalizedTarget["projectId"])!;
        var result = ProjectArchiver.Archive(project, _root, _clock.UtcNow);
        project.Status = ProjectRecord.ArchivedStatus;
        project.ArchivedAt = _clock.UtcNow;
        project.ArchivedPath = result.NewPath;
        _registry.Update(project);
        sink.Record(EventTypes.ProjectArchived, new { projectId = project.Id, slug = project.Slug, oldPath = result.OldPath, newPath = result.NewPath, manifestPath = result.ManifestPath, manifestHash = result.ManifestHash, files = result.Files, recoverUntil = result.RecoverUntil, proposalId = p.ProposalId });
        return ExecutionResult.Ok($"Archived '{project.Name}' to {result.NewPath} ({result.Files} files, manifest {result.ManifestHash[..12]}); restorable until {result.RecoverUntil:yyyy-MM-dd}", new Dictionary<string, string> { ["archivedPath"] = result.NewPath, ["manifestPath"] = result.ManifestPath });
    }

    private ExecutionResult RestoreProject(Proposal p, Decision d, IExecutionSink sink)
    {
        var project = _registry.ById(d.NormalizedTarget["projectId"])!;
        var (restoredPath, problems) = ProjectArchiver.Restore(project, _clock.UtcNow);
        project.Status = ProjectRecord.ActiveStatus;
        project.ArchivedAt = null;
        project.ArchivedPath = null;
        _registry.Update(project);
        sink.Record(EventTypes.ProjectRestored, new { projectId = project.Id, slug = project.Slug, path = restoredPath, problems, proposalId = p.ProposalId });
        var summary = problems.Count == 0 ? $"Restored '{project.Name}' to {restoredPath}; archive manifest verified" : $"Restored '{project.Name}' with {problems.Count} manifest problem(s): {string.Join("; ", problems)}";
        return ExecutionResult.Ok(summary, new Dictionary<string, string> { ["path"] = restoredPath });
    }

    private ExecutionResult ExportBackup(Proposal p, Decision d, IExecutionSink sink)
    {
        var path = d.NormalizedTarget["path"];
        if (File.Exists(path))
        {
            var stamp = _clock.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + stamp + ".zip");
        }
        var result = BackupService.Export(_root, _registry.All, path, _clock.UtcNow);
        sink.Record(EventTypes.BackupExported, new { path = result.ZipPath, files = result.Files, manifestHash = result.ManifestHash, proposalId = p.ProposalId });
        var verification = BackupService.Verify(result.ZipPath);
        sink.Record(EventTypes.BackupVerified, new { path = result.ZipPath, ok = verification.Ok, problems = verification.Problems });
        return verification.Ok
            ? ExecutionResult.Ok($"Exported and verified backup: {result.Files} files → {result.ZipPath}", new Dictionary<string, string> { ["path"] = result.ZipPath, ["manifestHash"] = result.ManifestHash })
            : ExecutionResult.Fail("Backup written but verification failed: " + string.Join("; ", verification.Problems));
    }

    private ExecutionResult MoveNote(Proposal p, Decision d, IExecutionSink sink)
    {
        var from = _registry.ById(d.NormalizedTarget["projectId"])!;
        var to = _registry.ById(d.NormalizedTarget["toProjectId"])!;
        var found = ProjectNoteStore.Find(from.RootPath, d.NormalizedTarget["noteId"]) ?? throw new FileNotFoundException("Note vanished.");
        var note = found.Note;
        note.ProjectId = to.Id;
        var written = ProjectNoteStore.WriteNew(to.RootPath, note);

        // Version history travels with the note; the source keeps a pointer so the move itself is traceable from either side.
        var fromVersions = Path.Combine(ProjectLayout.VersionsDirectory(from.RootPath), note.Id);
        if (Directory.Exists(fromVersions))
        {
            var toVersions = Path.Combine(ProjectLayout.VersionsDirectory(to.RootPath), note.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(toVersions)!);
            if (Directory.Exists(toVersions)) foreach (var f in Directory.EnumerateFiles(fromVersions)) File.Move(f, Path.Combine(toVersions, Path.GetFileName(f)), overwrite: false);
            else Directory.Move(fromVersions, toVersions);
        }
        var movedDir = Path.Combine(ProjectLayout.VersionsDirectory(from.RootPath), note.Id);
        Directory.CreateDirectory(movedDir);
        AtomicFile.WriteAllText(Path.Combine(movedDir, "moved.md"), File.ReadAllText(found.Path));
        AtomicFile.WriteAllText(Path.Combine(movedDir, "moved-to.txt"), $"{to.Slug}\n{written.Path}\n{_clock.UtcNow:O}\n");
        File.Delete(found.Path);
        sink.Record(EventTypes.NoteMoved, new { noteId = note.Id, fromProjectId = from.Id, toProjectId = to.Id, fromPath = found.Path, toPath = written.Path, sha256 = written.Sha256, proposalId = p.ProposalId });
        return ExecutionResult.Ok($"Moved note {note.Id} from {from.Slug} to {to.Slug}; a copy of the last version stays under {from.Slug}/.orchestrator", new Dictionary<string, string> { ["path"] = written.Path, ["projectId"] = to.Id, ["fromProjectId"] = from.Id });
    }

    private ExecutionResult DeleteProject(Proposal p, Decision d, IExecutionSink sink)
    {
        var project = _registry.ById(d.NormalizedTarget["projectId"])!;
        var now = _clock.UtcNow;
        Directory.CreateDirectory(_root.BackupsDirectory);
        var safety = Path.Combine(_root.BackupsDirectory, $"deleted-{project.Slug}-{now:yyyyMMdd'T'HHmmss'Z'}.zip");
        var files = Directory.EnumerateFiles(project.RootPath, "*", SearchOption.AllDirectories).Count();
        System.IO.Compression.ZipFile.CreateFromDirectory(project.RootPath, safety, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);
        Directory.Delete(project.RootPath, recursive: true);
        project.Status = ProjectRecord.DeletedStatus;
        project.ArchivedAt = now;
        project.ArchivedPath = null;
        _registry.Update(project);
        sink.Record(EventTypes.ProjectDeleted, new { projectId = project.Id, slug = project.Slug, path = project.RootPath, files, safetyBackup = safety, proposalId = p.ProposalId, by = p.ProposedBy });
        return ExecutionResult.Ok($"Deleted project '{project.Name}' ({files} files). A safety zip is at {safety}; nothing else remains.", new Dictionary<string, string> { ["safetyBackup"] = safety });
    }

    // ----------------------------------------------------------------------------------------
    // Journal
    // ----------------------------------------------------------------------------------------

    private void WriteJournal(ExecutionJournalEntry entry)
    {
        Directory.CreateDirectory(_root.ExecutionsDirectory);
        AtomicFile.WriteAllText(JournalPath(_root, entry.ProposalId), JsonSerializer.Serialize(entry, RelayJson.Indented));
    }

    private ExecutionJournalEntry? ReadJournal(string proposalId)
    {
        var text = AtomicFile.ReadAllTextIfExists(JournalPath(_root, proposalId));
        if (text is null) return null;
        try { return JsonSerializer.Deserialize<ExecutionJournalEntry>(text, RelayJson.Indented); } catch (JsonException) { return null; }
    }

    public static string JournalPath(DataRoot root, string proposalId) => Path.Combine(root.ExecutionsDirectory, proposalId + ".json");

    /// <summary>Journal entries that started but never finished: an operation was in flight when the process died.</summary>
    public static IReadOnlyList<ExecutionJournalEntry> FindInterrupted(DataRoot root)
    {
        if (!Directory.Exists(root.ExecutionsDirectory)) return [];
        var list = new List<ExecutionJournalEntry>();
        foreach (var file in Directory.EnumerateFiles(root.ExecutionsDirectory, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<ExecutionJournalEntry>(File.ReadAllText(file), RelayJson.Indented);
                if (entry is not null && entry.CompletedAt is null) list.Add(entry);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list;
    }
}
