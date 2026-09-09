using Relay.Core.Orchestration;
using Relay.Core.SelfChange;
using Relay.Core.Storage;

namespace Relay.Core.Tools;

/// <summary>
/// Where built tools live. Drafts are plain files under <c>staging\tools</c> that the builder rewrites as
/// it drafts and tests; a promoted tool is a file under <c>tools</c> written by exactly one change set, so
/// the user can revert it from the Relay panel like any other change Relay made to itself, and the
/// revert removes the tool. Built-in tool names are reserved.
/// </summary>
public sealed class ToolStore
{
    private readonly DataRoot _root;
    private readonly ChangeSetStore _changes;

    public ToolStore(DataRoot root, ChangeSetStore changes)
    {
        _root = root;
        _changes = changes;
    }

    public string DraftPath(string name) => Path.Combine(_root.ToolDraftsDirectory, name + ".json");
    public string PromotedPath(string name) => Path.Combine(_root.ToolsDirectory, name + ".json");

    /// <summary>Names no build may take: the built-in read-only tools and every promoted tool.</summary>
    public IReadOnlyList<string> ReservedNames() => [.. ToolBroker.Descriptors.Select(d => d.Name), .. Promoted().Select(p => p.Name)];

    public bool IsPromoted(string name) => ToolPackage.ValidName(name) && File.Exists(PromotedPath(name));

    public ToolPackage? Promoted(string name) => IsPromoted(name) ? Load(PromotedPath(name)) : null;

    /// <summary>Every promoted tool, by name. Unreadable files are skipped (and reported by <see cref="Problems"/>).</summary>
    public IReadOnlyList<ToolPackage> Promoted()
    {
        if (!Directory.Exists(_root.ToolsDirectory)) return [];
        return Directory.EnumerateFiles(_root.ToolsDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(Load).Where(p => p is not null).Select(p => p!)
            .Where(p => string.Equals(Path.GetFileNameWithoutExtension(PromotedPath(p.Name)), p.Name, StringComparison.Ordinal))
            .ToList();
    }

    public IReadOnlyList<ToolDescriptor> Descriptors() => Promoted().Select(p => p.Descriptor).ToList();

    public IReadOnlyList<string> Problems()
    {
        if (!Directory.Exists(_root.ToolsDirectory)) return [];
        return Directory.EnumerateFiles(_root.ToolsDirectory, "*.json")
            .Where(f => Load(f) is not { } p || !string.Equals(p.Name, Path.GetFileNameWithoutExtension(f), StringComparison.Ordinal))
            .Select(f => $"Promoted tool file {Path.GetFileName(f)} is unreadable or misnamed and is ignored.")
            .ToList();
    }

    public ToolPackage? Draft(string name) => ToolPackage.ValidName(name) && File.Exists(DraftPath(name)) ? Load(DraftPath(name)) : null;

    public string SaveDraft(ToolPackage package)
    {
        if (!ToolPackage.ValidName(package.Name)) throw new ArgumentException("invalid tool name", nameof(package));
        Directory.CreateDirectory(_root.ToolDraftsDirectory);
        var path = DraftPath(package.Name);
        AtomicFile.WriteAllText(path, package.ToJson());
        return path;
    }

    /// <summary>
    /// Moves a tested draft into <c>tools</c> as one change set. Refuses an untested draft (the tests must have
    /// passed for the exact source being promoted) and a name that is already taken.
    /// </summary>
    public ChangeSetResult Promote(string name, string reason, DateTimeOffset now, string? taskId, string? proposalId)
    {
        var draft = Draft(name);
        if (draft is null) return new ChangeSetResult(false, null, $"There is no draft tool named '{name}'.");
        if (!draft.Tested) return new ChangeSetResult(false, null, $"Draft '{name}' has not passed its tests for the source being promoted.");
        if (IsPromoted(name) || ToolBroker.Descriptors.Any(d => d.Name == name)) return new ChangeSetResult(false, null, $"A tool named '{name}' already exists.");
        var promoted = new ToolPackage
        {
            Name = draft.Name, Description = draft.Description, Arguments = draft.Arguments, HostFunctionNames = draft.HostFunctionNames, Source = draft.Source, Tests = draft.Tests,
            BuiltBy = draft.BuiltBy, TaskId = draft.TaskId, Justification = draft.Justification, DraftedAt = draft.DraftedAt, TestedSha256 = draft.TestedSha256, TestedAt = draft.TestedAt, PromotedAt = now,
        };
        Directory.CreateDirectory(_root.ToolsDirectory);
        var result = _changes.Apply(ChangeKinds.Tool, PromotedPath(name), promoted.ToJson(), reason, now, taskId, proposalId);
        if (result.Ok) { try { File.Delete(DraftPath(name)); } catch (IOException) { } }
        return result;
    }

    private static ToolPackage? Load(string path)
    {
        var text = AtomicFile.ReadAllTextIfExists(path);
        return text is null ? null : ToolPackage.FromJson(text);
    }
}
