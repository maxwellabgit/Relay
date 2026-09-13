using Relay.Core.SelfChange;
using Relay.Core.Storage;

namespace Relay.Core.Workflows;

/// <summary>
/// Where workflows live. Drafts are plain files under <c>staging\workflows</c>; a promoted workflow is a
/// file under <c>workflows</c> written by exactly one change set, so the user can revert it like any other
/// change Relay made to itself, and the revert removes the workflow.
/// </summary>
public sealed class WorkflowStore
{
    private readonly DataRoot _root;
    private readonly ChangeSetStore _changes;

    public WorkflowStore(DataRoot root, ChangeSetStore changes)
    {
        _root = root;
        _changes = changes;
    }

    public string DraftPath(string name) => Path.Combine(_root.WorkflowDraftsDirectory, name + ".json");
    public string PromotedPath(string name) => Path.Combine(_root.WorkflowsDirectory, name + ".json");

    public IReadOnlyList<string> ReservedNames() => Promoted().Select(p => p.Name).ToList();

    public bool IsPromoted(string name) => WorkflowDefinition.ValidName(name) && File.Exists(PromotedPath(name));

    public WorkflowDefinition? Promoted(string name) => IsPromoted(name) ? Load(PromotedPath(name)) : null;

    public IReadOnlyList<WorkflowDefinition> Promoted()
    {
        if (!Directory.Exists(_root.WorkflowsDirectory)) return [];
        return Directory.EnumerateFiles(_root.WorkflowsDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(Load).Where(p => p is not null).Select(p => p!)
            .Where(p => string.Equals(Path.GetFileNameWithoutExtension(PromotedPath(p.Name)), p.Name, StringComparison.Ordinal))
            .ToList();
    }

    public IReadOnlyList<WorkflowDescriptor> Descriptors() => Promoted().Select(p => p.Descriptor).ToList();

    public IReadOnlyList<string> Problems()
    {
        if (!Directory.Exists(_root.WorkflowsDirectory)) return [];
        return Directory.EnumerateFiles(_root.WorkflowsDirectory, "*.json")
            .Where(f => Load(f) is not { } p || !string.Equals(p.Name, Path.GetFileNameWithoutExtension(f), StringComparison.Ordinal))
            .Select(f => $"Promoted workflow file {Path.GetFileName(f)} is unreadable or misnamed and is ignored.")
            .ToList();
    }

    public WorkflowDefinition? Draft(string name) => WorkflowDefinition.ValidName(name) && File.Exists(DraftPath(name)) ? Load(DraftPath(name)) : null;

    public string SaveDraft(WorkflowDefinition definition)
    {
        if (!WorkflowDefinition.ValidName(definition.Name)) throw new ArgumentException("invalid workflow name", nameof(definition));
        Directory.CreateDirectory(_root.WorkflowDraftsDirectory);
        var path = DraftPath(definition.Name);
        AtomicFile.WriteAllText(path, definition.ToJson());
        return path;
    }

    /// <summary>
    /// Moves a tested draft into <c>workflows</c> as one change set. Refuses an untested draft (the dry-run
    /// must have passed for the exact definition being promoted) and a name that is already taken.
    /// </summary>
    public ChangeSetResult Promote(string name, string reason, DateTimeOffset now, string? taskId, string? proposalId)
    {
        var draft = Draft(name);
        if (draft is null) return new ChangeSetResult(false, null, $"There is no draft workflow named '{name}'.");
        if (!draft.Tested) return new ChangeSetResult(false, null, $"Draft '{name}' has not passed its dry-run for the definition being promoted.");
        if (IsPromoted(name)) return new ChangeSetResult(false, null, $"A workflow named '{name}' already exists.");
        var promoted = new WorkflowDefinition
        {
            Name = draft.Name, Description = draft.Description, Version = draft.Version, Steps = draft.Steps,
            BuiltBy = draft.BuiltBy, TaskId = draft.TaskId, Justification = draft.Justification, DraftedAt = draft.DraftedAt,
            TestedSha256 = draft.TestedSha256, TestedAt = draft.TestedAt, PromotedAt = now,
        };
        Directory.CreateDirectory(_root.WorkflowsDirectory);
        var result = _changes.Apply(ChangeKinds.Workflow, PromotedPath(name), promoted.ToJson(), reason, now, taskId, proposalId);
        if (result.Ok) { try { File.Delete(DraftPath(name)); } catch (IOException) { } }
        return result;
    }

    private static WorkflowDefinition? Load(string path)
    {
        var text = AtomicFile.ReadAllTextIfExists(path);
        return text is null ? null : WorkflowDefinition.FromJson(text);
    }
}
