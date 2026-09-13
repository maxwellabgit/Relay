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
            Actions.SupersedeNote => t.ContainsKey("newText")
                ? ($"Update the {Get("type", "decision")} in '{slug}'", $"New text: {Truncate(Get("newText"), 300)}\nThe earlier note {Short(Get("noteId"))} keeps its text with status 'superseded'; the new note records what it replaces." + (t.ContainsKey("sourceExcerptId") ? $"\nSource: excerpt {Short(Get("sourceExcerptId"))}" : ""))
                : ($"Mark note {Short(Get("noteId"))} superseded by {Short(Get("supersededBy"))}", "The older note keeps its text with status 'superseded'; the newer one records what it replaces."),
            Actions.LaunchWorker => ($"Run a sandboxed worker: {Get("task")} '{slug}'", $"Objective: {Get("objective")}\nNetwork: {Get("network", "false")}\nThe worker reads project notes through the broker and writes only to its staging out folder. Applying its output is a separate approval."),
            Actions.ApplyPatch => ($"Apply worker output to '{slug}/{Get("destination")}'", $"From run {Short(Get("runId"))} file {Get("output")}. Existing files are versioned before being replaced."),
            Actions.ExportBackup => ("Export a verified backup", $"Zip: {Get("path", "(backups folder)")}\nContains the data root and every active project with a hashed manifest; verified after writing."),
            Actions.DeleteProject => ($"Permanently delete project '{slug}'", "Removes the folder and every note in it. This is not an archive: nothing can be restored afterwards. A verified backup of the project is written to the backups folder first."),
            Actions.MoveNote => ($"Move note {Short(Get("noteId"))} from '{slug}' to '{Get("toProjectSlug", Get("toProjectId", Get("toProject")))}'",
                "The note file and its version history move to the destination project; the source keeps a pointer in .orchestrator." + (t.ContainsKey("toProjectPlanned") ? "\nThe destination is created by a prerequisite in this task; this move runs only after it has." : "")),
            Actions.ModelRequest when Get("conversation", "").Length > 0 => ($"Continue the conversation with external model '{Get("profile")}' (turn {Get("turn", "?")})", $"Next message: {Get("objective")}\nNo new local sources leave the machine; the conversation runs under the approval of request {Short(Get("conversation"))}.\nThe exact turn is hashed and logged; the reply is stored as a source artifact."),
            Actions.ModelRequest => ($"Send a package to external model '{Get("profile")}'", $"Objective: {Get("objective")}\nReferences leaving the machine: {(Get("refs", "").Length == 0 ? "none" : Get("refs"))}\nBudget: {Get("budgetTokens")} tokens · online search: {Get("allowSearch", "false")}\nThe exact package is hashed and logged; the response is stored as a source artifact."),
            Actions.UpdatePreference => ($"Change preference {Get("key")}", $"New value: {Get("value")}\nApplied as a reversible change set to config\\preferences.json." + Contract(t)),
            Actions.UpdatePrompt => ($"Change the '{Get("name")}' prompt fragment", $"New text ({Get("content").Length} chars): {Truncate(Get("content"), 300)}\nApplied as a reversible change set; the previous text is kept." + Contract(t)),
            Actions.AddTool => ($"Add the tool '{Get("name")}'", $"{Get("description", "A tool Relay built for itself.")}\nRuns only in the worker sandbox; reaches the machine through: {(Get("hostFunctions", "").Length == 0 ? "nothing" : Get("hostFunctions"))}.\nTests passed in the sandbox: {Get("tests", "?")} · source {Short(Get("sourceSha256"))}\nOne reversible change set; reverting removes the tool." + Contract(t)),
            Actions.AddWorkflow => ($"Add the workflow '{Get("name")}'", $"{Get("description", "A workflow Relay authored for itself.")}\nVersion {Get("version", "1")} · {Get("steps", "?")} step(s) · definition {Short(Get("definitionSha256"))}\nRuns through the same task loop (waits and resumes are the runtime's).\nOne reversible change set; reverting removes the workflow." + Contract(t)),
            _ => (p.Action, string.Join("\n", p.Target.Select(kv => $"{kv.Key}: {kv.Value}"))),
        };
        return (title, detail + (string.IsNullOrWhiteSpace(p.Reason) ? "" : $"\n\nWhy: {p.Reason}"));
    }

    /// <summary>The improvement contract of a self-change, rendered in full so the card states benefit, permissions, scope, and acceptance.</summary>
    private static string Contract(IReadOnlyDictionary<string, string> t)
    {
        var lines = new List<string>();
        if (t.TryGetValue("benefit", out var b) && b.Length > 0) lines.Add($"Benefit: {b}");
        if (t.TryGetValue("permissions", out var p) && p.Length > 0) lines.Add($"Permissions: {p}");
        if (t.TryGetValue("scope", out var s) && s.Length > 0) lines.Add($"Scope: {s}");
        if (t.TryGetValue("acceptance", out var a) && a.Length > 0) lines.Add($"Acceptance: {a}");
        return lines.Count == 0 ? "" : "\n" + string.Join("\n", lines);
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
        Actions.SupersedeNote => ["newText", "type"],
        Actions.MoveNote => ["toProjectId"],
        Actions.ModelRequest => ["objective", "profile", "budgetTokens", "allowSearch"],
        Actions.UpdatePreference => ["value"],
        Actions.UpdatePrompt => ["content"],
        _ => [],
    };

    private static string Short(string id) => id.Length > 10 ? "…" + id[^8..] : id;
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
