namespace Relay.Core.Policy;

/// <summary>Plain-language rendering of proposals for Review. The target values are shown verbatim; nothing is paraphrased away.</summary>
public static class ProposalText
{
    public static (string Title, string Detail) Describe(Proposal p, Decision? d)
    {
        var t = d is { NormalizedTarget.Count: > 0 } ? d.NormalizedTarget : p.Target;
        string Get(string key, string fallback = "?") => t.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
        var slug = Get("projectSlug", Get("project", Get("projectId")));

        var (title, detail) = p.Action switch
        {
            Actions.CreateProject => ($"Create project '{Get("name")}'", $"Folder: {Get("path", "(first registered workspace)/" + Get("slug"))}\nSlug: {Get("slug")}\nStandard layout: notes, decisions, tasks, conversations, artifacts, .orchestrator"),
            Actions.ArchiveProject => ($"Archive project '{slug}'", "Moves the whole folder into the recoverable archive with a manifest of every file hash. Nothing is deleted; restorable for 90 days."),
            Actions.RestoreProject => ($"Restore project '{slug}'", "Moves the archived folder back and verifies the manifest; any file that changed while archived is reported."),
            Actions.RenameProject => ($"Rename project '{slug}' → '{Get("newName", Get("newSlug"))}'", $"New slug: {Get("newSlug")}" + (t.ContainsKey("newPath") ? $"\nNew folder: {Get("newPath")}" : "") + "\nThe old name stays as an alias."),
            Actions.RouteNote => ($"File note {Short(Get("noteId"))} under '{slug}'", $"Adds a new note file to {slug}/{Notes.NoteTypes.Folder(Get("type", "idea"))}. Confidence {Get("confidence", "n/a")}. Modifies nothing existing."),
            Actions.CreateDraftNote => ("Save a draft note", $"Text ({Get("text").Length} chars): {Truncate(Get("text"), 200)}"),
            Actions.ModifyNote => ($"Modify note {Short(Get("noteId"))} in '{slug}'", (t.ContainsKey("status") ? $"Status → {Get("status")}\n" : "") + (t.ContainsKey("body") ? $"New text: {Truncate(Get("body"), 300)}\n" : "") + "The previous version is kept under .orchestrator/versions."),
            Actions.SupersedeNote => ($"Mark note {Short(Get("noteId"))} superseded by {Short(Get("supersededBy"))}", "The older note keeps its text with status 'superseded'; the newer one records what it replaces."),
            Actions.LaunchWorker => ($"Run a sandboxed worker: {Get("task")} '{slug}'", $"Objective: {Get("objective")}\nNetwork: {Get("network", "false")}\nThe worker reads project notes through the broker and writes only to its staging out folder. Applying its output is a separate approval."),
            Actions.ApplyPatch => ($"Apply worker output to '{slug}/{Get("destination")}'", $"From run {Short(Get("runId"))} file {Get("output")}. Existing files are versioned before being replaced."),
            Actions.ExportBackup => ("Export a verified backup", $"Zip: {Get("path", "(backups folder)")}\nContains the data root and every active project with a hashed manifest; verified after writing."),
            Actions.DeleteProject => ($"Permanently delete project '{slug}'", "Prohibited: Relay archives, it never deletes."),
            _ => (p.Action, string.Join("\n", p.Target.Select(kv => $"{kv.Key}: {kv.Value}"))),
        };
        return (title, detail + (string.IsNullOrWhiteSpace(p.Reason) ? "" : $"\n\nWhy: {p.Reason}"));
    }

    /// <summary>Target keys the user may change in Review before re-proposing.</summary>
    public static IReadOnlyList<string> EditableKeys(string action) => action switch
    {
        Actions.CreateProject => ["name", "slug", "parent"],
        Actions.RenameProject => ["newName", "newSlug"],
        Actions.LaunchWorker => ["objective"],
        Actions.ApplyPatch => ["destination"],
        Actions.ExportBackup => ["path"],
        Actions.ModifyNote => ["body", "status"],
        _ => [],
    };

    private static string Short(string id) => id.Length > 10 ? "…" + id[^8..] : id;
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
