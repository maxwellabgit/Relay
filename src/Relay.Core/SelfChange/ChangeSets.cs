using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;

namespace Relay.Core.SelfChange;

/// <summary>What kind of thing Relay changed about itself. The permission broker and approval mechanism are never in scope.</summary>
public static class ChangeKinds
{
    public const string Preference = "preference";
    public const string Prompt = "prompt";
    public const string Routing = "routing";
    /// <summary>A tool Relay built was promoted (the file appears) or withdrawn; reverting a promotion removes the tool.</summary>
    public const string Tool = "tool";
    /// <summary>A workflow definition was promoted (the file appears) or withdrawn; reverting a promotion removes the workflow.</summary>
    public const string Workflow = "workflow";
    public static readonly string[] All = [Preference, Prompt, Routing, Tool, Workflow];
}

/// <summary>
/// One reversible change to Relay's own configuration: the file, its content before and after, why,
/// and which task asked for it. Reverting restores the before image and marks the set reverted; the
/// record itself is never deleted.
/// </summary>
public sealed class ChangeSet
{
    [JsonPropertyName("changeSetId")] public required string ChangeSetId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("before")] public string? Before { get; init; }
    [JsonPropertyName("beforeSha256")] public string? BeforeSha256 { get; init; }
    [JsonPropertyName("after")] public required string After { get; init; }
    [JsonPropertyName("afterSha256")] public required string AfterSha256 { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("taskId")] public string? TaskId { get; init; }
    [JsonPropertyName("proposalId")] public string? ProposalId { get; init; }
    [JsonPropertyName("appliedAt")] public required DateTimeOffset AppliedAt { get; init; }
    [JsonPropertyName("revertedAt")] public DateTimeOffset? RevertedAt { get; set; }
    [JsonPropertyName("revertReason")] public string? RevertReason { get; set; }

    [JsonIgnore] public bool Reverted => RevertedAt is not null;
}

public sealed record ChangeSetResult(bool Ok, ChangeSet? ChangeSet, string? Error);

/// <summary>
/// The only writer of Relay's own configuration files under the data root. Every write is a change set
/// with a stored before image; tests and the user can revert any of them, newest first.
/// </summary>
public sealed class ChangeSetStore
{
    private readonly DataRoot _root;

    public ChangeSetStore(DataRoot root) => _root = root;

    /// <summary>Paths a change set may touch: preferences, prompt fragments, promoted tools and workflows. Settings that gate authority are out of scope by construction.</summary>
    public bool IsAllowedPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return string.Equals(full, System.IO.Path.GetFullPath(_root.PreferencesPath), StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(System.IO.Path.GetFullPath(_root.PromptsDirectory) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(System.IO.Path.GetFullPath(_root.ToolsDirectory) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(System.IO.Path.GetFullPath(_root.WorkflowsDirectory) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public ChangeSetResult Apply(string kind, string path, string newContent, string reason, DateTimeOffset now, string? taskId = null, string? proposalId = null)
    {
        if (!ChangeKinds.All.Contains(kind)) return new ChangeSetResult(false, null, $"Unknown change kind '{kind}'.");
        if (!IsAllowedPath(path)) return new ChangeSetResult(false, null, "Change sets may only touch preferences, prompt fragments, promoted tools and workflows.");
        var before = AtomicFile.ReadAllTextIfExists(path);
        if (string.Equals(before, newContent, StringComparison.Ordinal)) return new ChangeSetResult(false, null, "No change: the content is already in effect.");
        var set = new ChangeSet
        {
            ChangeSetId = Ulid.NewUlid(now),
            Kind = kind,
            Path = path,
            Before = before,
            BeforeSha256 = before is null ? null : Sha(before),
            After = newContent,
            AfterSha256 = Sha(newContent),
            Reason = reason,
            TaskId = taskId,
            ProposalId = proposalId,
            AppliedAt = now,
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(_root.ChangeSetsDirectory);
        // Record first, then apply: a crash between the two leaves a change set whose after image is not on disk, which Revert tolerates.
        AtomicFile.WriteAllText(PathFor(set.ChangeSetId), JsonSerializer.Serialize(set, RelayJson.Indented));
        AtomicFile.WriteAllText(path, newContent);
        return new ChangeSetResult(true, set, null);
    }

    public ChangeSetResult Revert(string changeSetId, string reason, DateTimeOffset now)
    {
        var set = Read(changeSetId);
        if (set is null) return new ChangeSetResult(false, null, $"No change set {changeSetId}.");
        if (set.Reverted) return new ChangeSetResult(false, set, "Already reverted.");
        var current = AtomicFile.ReadAllTextIfExists(set.Path);
        if (current is not null && Sha(current) != set.AfterSha256)
            return new ChangeSetResult(false, set, "The file changed after this change set; revert the later change sets first.");
        if (set.Before is null) { if (File.Exists(set.Path)) File.Delete(set.Path); }
        else AtomicFile.WriteAllText(set.Path, set.Before);
        set.RevertedAt = now;
        set.RevertReason = reason;
        AtomicFile.WriteAllText(PathFor(set.ChangeSetId), JsonSerializer.Serialize(set, RelayJson.Indented));
        return new ChangeSetResult(true, set, null);
    }

    /// <summary>Reverts every applied change set, newest first. Used by tests so a self-improvement never outlives the test that asked for it.</summary>
    public IReadOnlyList<ChangeSetResult> RevertAll(string reason, DateTimeOffset now)
        => All().Where(s => !s.Reverted).OrderByDescending(s => s.ChangeSetId, StringComparer.Ordinal).Select(s => Revert(s.ChangeSetId, reason, now)).ToList();

    public ChangeSet? Read(string changeSetId)
    {
        var text = AtomicFile.ReadAllTextIfExists(PathFor(changeSetId));
        if (text is null) return null;
        try { return JsonSerializer.Deserialize<ChangeSet>(text, RelayJson.Indented); } catch (JsonException) { return null; }
    }

    public IReadOnlyList<ChangeSet> All()
    {
        if (!Directory.Exists(_root.ChangeSetsDirectory)) return [];
        var list = new List<ChangeSet>();
        foreach (var file in Directory.EnumerateFiles(_root.ChangeSetsDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var set = JsonSerializer.Deserialize<ChangeSet>(File.ReadAllText(file), RelayJson.Indented);
                if (set is not null) list.Add(set);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return list;
    }

    private string PathFor(string id) => System.IO.Path.Combine(_root.ChangeSetsDirectory, id + ".json");

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
