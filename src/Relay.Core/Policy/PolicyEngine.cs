using Relay.Core.Notes;
using Relay.Core.Projects;
using Relay.Core.Storage;
using Relay.Core.Workspaces;

namespace Relay.Core.Policy;

/// <summary>Everything the policy engine may consult. Read-only views; the engine never writes.</summary>
public sealed class PolicyWorld
{
    public required ProjectRegistry Registry { get; init; }
    public required WorkspaceRoots Roots { get; init; }
    public required DataRoot DataRoot { get; init; }
    public required Func<string, bool> DraftNoteExists { get; init; }
    public required Func<string, string, bool> ProjectNoteExists { get; init; }
    public bool WorkersEnabled { get; init; } = true;
    public Func<string, bool>? AgentRunHasOutput { get; init; }
    /// <summary>Names of configured external model profiles; a model.request proposal must name one.</summary>
    public IReadOnlyList<string> ExternalProfiles { get; init; } = [];
    /// <summary>Whether a reference (note id, excerpt id, capture id) resolves to something Relay holds; external packages may only carry these.</summary>
    public Func<string, bool>? ReferenceExists { get; init; }
    /// <summary>Prompt fragment names that may be changed through change sets.</summary>
    public IReadOnlyList<string> PromptFragments { get; init; } = ["planner", "judge"];
    /// <summary>Origin of the task the proposal belongs to. Some actions may only be proposed from a direct request; null means unknown and is treated as direct.</summary>
    public Tasks.TaskOrigin? Origin { get; init; }
}

/// <summary>
/// Deterministic authority (contract §7–§8). Maps an action to its tier, validates the target
/// against the registry and path rules, and recomputes whether approval is required. The
/// proposer's own <c>requiresApproval</c> flag is ignored except that claiming "no approval"
/// for a Tier B action is recorded as a reason the user can see.
/// </summary>
public static class PolicyEngine
{
    public static Tier TierOf(string action) => action switch
    {
        Actions.CreateDraftNote or Actions.RouteNote => Tier.Automatic,
        Actions.CreateProject or Actions.ModifyNote or Actions.SupersedeNote or Actions.MoveNote or Actions.RenameProject
            or Actions.ArchiveProject or Actions.RestoreProject or Actions.DeleteProject or Actions.LaunchWorker or Actions.ApplyPatch or Actions.ExportBackup
            or Actions.ModelRequest or Actions.UpdatePreference or Actions.UpdatePrompt => Tier.RequiresApproval,
        _ => Tier.Prohibited,
    };

    public static Decision Decide(Proposal p, PolicyWorld w)
    {
        var tier = TierOf(p.Action);
        var reasons = new List<string>();
        if (tier == Tier.Prohibited)
        {
            reasons.Add(Actions.Prohibited.Contains(p.Action) ? $"'{p.Action}' is prohibited in this release." : $"Unknown action '{p.Action}'.");
            return Decision.Deny(tier, reasons.ToArray());
        }
        if (p.SourceEventIds.Count == 0 && p.ProposedBy != Producers.User)
        {
            return Decision.Deny(tier, "Proposal cites no source event; every proposal must trace to an instruction or capture.");
        }
        if (w.Origin is { } origin && origin != Tasks.TaskOrigin.Direct && Actions.DirectOnly.Contains(p.Action))
        {
            return Decision.Deny(tier, $"'{p.Action}' may only be proposed from a direct request, never from something overheard or a follow-up.");
        }
        if (tier == Tier.RequiresApproval && !p.RequiresApproval && p.ProposedBy != Producers.User)
        {
            reasons.Add("Proposer claimed no approval was needed; policy requires approval for this action.");
        }

        var target = new Dictionary<string, string>(p.Target, StringComparer.Ordinal);
        var problems = p.Action switch
        {
            Actions.CreateDraftNote => ValidateCreateDraftNote(target),
            Actions.RouteNote => ValidateRouteNote(target, w),
            Actions.CreateProject => ValidateCreateProject(target, w),
            Actions.ModifyNote or Actions.SupersedeNote => ValidateNoteChange(p.Action, target, w),
            Actions.RenameProject => ValidateRenameProject(target, w),
            Actions.ArchiveProject => ValidateArchive(target, w),
            Actions.RestoreProject => ValidateRestore(target, w),
            Actions.LaunchWorker => ValidateLaunchWorker(target, w),
            Actions.ApplyPatch => ValidateApplyPatch(target, w),
            Actions.ExportBackup => ValidateExportBackup(target, w),
            Actions.MoveNote => ValidateMoveNote(target, w),
            Actions.DeleteProject => ValidateDelete(target, w),
            Actions.ModelRequest => ValidateModelRequest(target, w),
            Actions.UpdatePreference => ValidateUpdatePreference(target),
            Actions.UpdatePrompt => ValidateUpdatePrompt(target, w),
            _ => ["Unhandled action."],
        };
        if (problems.Count > 0)
        {
            reasons.AddRange(problems);
            return new Decision(DecisionOutcome.Deny, tier, reasons, target);
        }

        var outcome = tier == Tier.Automatic ? DecisionOutcome.Allow : DecisionOutcome.NeedsApproval;
        if (reasons.Count == 0) reasons.Add(tier == Tier.Automatic ? "Automatically allowed: staging or additive write only." : "Controlled write: requires your approval.");
        return new Decision(outcome, tier, reasons, target);
    }

    private static List<string> ValidateCreateDraftNote(Dictionary<string, string> t)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(t.GetValueOrDefault("text"))) problems.Add("target.text is required.");
        if (!NoteTypes.All.Contains(t.GetValueOrDefault("type", NoteTypes.Idea))) problems.Add("target.type is not a known note type.");
        t.TryAdd("type", NoteTypes.Idea);
        return problems;
    }

    private static List<string> ValidateRouteNote(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var noteId = t.GetValueOrDefault("noteId");
        if (string.IsNullOrWhiteSpace(noteId)) problems.Add("target.noteId is required.");
        else if (!w.DraftNoteExists(noteId)) problems.Add($"Draft note {noteId} does not exist in staging.");
        var project = ResolveActiveProject(t, w, problems);
        if (project is not null && !Directory.Exists(project.RootPath)) problems.Add($"Project folder is missing: {project.RootPath}");
        if (t.TryGetValue("type", out var type) && !NoteTypes.All.Contains(type)) problems.Add("target.type is not a known note type.");
        if (t.TryGetValue("status", out var status) && status is not (NoteStatus.Active or NoteStatus.Disputed or NoteStatus.Draft)) problems.Add("target.status for a routed note must be active, disputed or draft.");
        if (t.TryGetValue("disputedWith", out var disputed) && project is not null)
        {
            foreach (var otherId in disputed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!w.ProjectNoteExists(project.Id, otherId)) problems.Add($"Disputed note {otherId} does not exist in project {project.Slug}.");
        }
        return problems;
    }

    private static List<string> ValidateCreateProject(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var name = t.GetValueOrDefault("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { problems.Add("target.name is required."); return problems; }
        if (name.Length > 80) problems.Add("target.name is longer than 80 characters.");
        var slug = t.GetValueOrDefault("slug");
        if (string.IsNullOrWhiteSpace(slug)) slug = Slug.From(name);
        if (!Slug.IsValid(slug)) problems.Add($"'{slug}' is not a valid slug (lower-case letters, digits, hyphens).");
        else if (w.Registry.SlugInUse(slug)) problems.Add($"An active project already uses the slug '{slug}'.");
        t["name"] = name;
        t["slug"] = slug;

        var roots = w.Roots.Registered;
        string? parent = t.GetValueOrDefault("parent");
        if (string.IsNullOrWhiteSpace(parent))
        {
            if (roots.Count == 0) { problems.Add("No project folder is known yet. Use New project… once to choose the folder projects live in."); return problems; }
            parent = roots[0].Path;
        }
        var check = w.Roots.Check(parent);
        if (!check.Ok) { problems.Add($"Parent folder rejected: {check.Reason}"); return problems; }
        if (PathGuard.IsWithin(check.Canonical, PathGuard.Canonicalize(w.DataRoot.ArchiveDirectory))) problems.Add("Projects cannot be created inside the archive.");
        var path = Path.Combine(check.Canonical, slug);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) problems.Add($"Folder already exists and is not empty: {path}");
        t["parent"] = check.Canonical;
        t["path"] = path;
        return problems;
    }

    private static List<string> ValidateNoteChange(string action, Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var project = ResolveActiveProject(t, w, problems);
        var noteId = t.GetValueOrDefault("noteId");
        if (string.IsNullOrWhiteSpace(noteId)) problems.Add("target.noteId is required.");
        else if (project is not null && !w.ProjectNoteExists(project.Id, noteId)) problems.Add($"Note {noteId} does not exist in project {project.Slug}.");
        if (action == Actions.ModifyNote && string.IsNullOrWhiteSpace(t.GetValueOrDefault("body")) && string.IsNullOrWhiteSpace(t.GetValueOrDefault("status")))
            problems.Add("modify_note needs target.body or target.status.");
        if (action == Actions.SupersedeNote)
        {
            // Either an existing replacement note, or the replacement's text, from which one new note is created in the same operation.
            var newId = t.GetValueOrDefault("supersededBy");
            var newText = t.GetValueOrDefault("newText")?.Trim();
            if (string.IsNullOrWhiteSpace(newId) && string.IsNullOrWhiteSpace(newText)) problems.Add("supersede_note needs target.supersededBy (an existing note) or target.newText (the replacement).");
            if (!string.IsNullOrWhiteSpace(newId) && !string.IsNullOrWhiteSpace(newText)) problems.Add("supersede_note takes either target.supersededBy or target.newText, not both.");
            if (!string.IsNullOrWhiteSpace(newId) && project is not null && !w.ProjectNoteExists(project.Id, newId)) problems.Add($"Note {newId} does not exist in project {project.Slug}.");
            if (!string.IsNullOrWhiteSpace(newText))
            {
                if (newText.Length > 2000) problems.Add("target.newText is longer than 2000 characters.");
                if (newText == noteId) problems.Add("target.newText must be the replacement text, not an id.");
                t["newText"] = newText;
                if (t.TryGetValue("type", out var type) && !NoteTypes.All.Contains(type)) problems.Add($"target.type '{type}' is not a note type.");
                if (t.TryGetValue("sourceExcerptId", out var excerptId) && !string.IsNullOrWhiteSpace(excerptId) && w.ReferenceExists is not null && !w.ReferenceExists(excerptId))
                    problems.Add($"target.sourceExcerptId {excerptId} does not name a stored excerpt or note.");
            }
        }
        if (t.TryGetValue("status", out var status) && status is not (NoteStatus.Active or NoteStatus.Disputed or NoteStatus.Superseded or NoteStatus.Archived or NoteStatus.Draft))
            problems.Add("target.status is not a known note status.");
        return problems;
    }

    private static List<string> ValidateRenameProject(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var project = ResolveActiveProject(t, w, problems);
        var newName = t.GetValueOrDefault("newName")?.Trim();
        var newSlug = t.GetValueOrDefault("newSlug");
        if (string.IsNullOrWhiteSpace(newName) && string.IsNullOrWhiteSpace(newSlug)) problems.Add("target.newName or target.newSlug is required.");
        if (string.IsNullOrWhiteSpace(newSlug) && !string.IsNullOrWhiteSpace(newName)) newSlug = Slug.From(newName);
        if (newSlug is not null)
        {
            if (!Slug.IsValid(newSlug)) problems.Add($"'{newSlug}' is not a valid slug.");
            else if (project is not null && newSlug != project.Slug && w.Registry.SlugInUse(newSlug)) problems.Add($"Slug '{newSlug}' is already in use.");
            t["newSlug"] = newSlug;
        }
        if (newName is not null) t["newName"] = newName;
        if (project is not null && newSlug is not null && newSlug != project.Slug)
        {
            var newPath = Path.Combine(Path.GetDirectoryName(project.RootPath)!, newSlug);
            var check = w.Roots.Check(newPath);
            if (!check.Ok) problems.Add($"New path rejected: {check.Reason}");
            else if (Directory.Exists(check.Canonical)) problems.Add($"Folder already exists: {check.Canonical}");
            else t["newPath"] = check.Canonical;
        }
        return problems;
    }

    private static List<string> ValidateArchive(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var project = ResolveActiveProject(t, w, problems);
        if (project is not null && !Directory.Exists(project.RootPath)) problems.Add($"Project folder is missing: {project.RootPath}");
        return problems;
    }

    private static List<string> ValidateRestore(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var key = t.GetValueOrDefault("projectId") ?? t.GetValueOrDefault("project");
        if (string.IsNullOrWhiteSpace(key)) { problems.Add("target.projectId is required."); return problems; }
        var project = w.Registry.Find(key);
        if (project is null) problems.Add($"No project matches '{key}'.");
        else if (project.IsActive) problems.Add($"Project '{project.Slug}' is not archived.");
        else if (project.ArchivedPath is null || !Directory.Exists(project.ArchivedPath)) problems.Add("Archived folder is missing.");
        else
        {
            t["projectId"] = project.Id;
            if (Directory.Exists(project.RootPath) && Directory.EnumerateFileSystemEntries(project.RootPath).Any()) problems.Add($"Original location is occupied: {project.RootPath}");
        }
        return problems;
    }

    private static List<string> ValidateLaunchWorker(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        if (!w.WorkersEnabled) problems.Add("Workers are disabled in settings.");
        var project = ResolveActiveProject(t, w, problems);
        if (project is not null && !project.Policy.AllowWorkers) problems.Add($"Project '{project.Slug}' does not allow workers.");
        var task = t.GetValueOrDefault("task");
        if (task is not "summarize") problems.Add("Only the 'summarize' worker task exists in this build.");
        if (string.Equals(t.GetValueOrDefault("network"), "true", StringComparison.OrdinalIgnoreCase)) problems.Add("Workers cannot be given network access in this build.");
        t["network"] = "false";
        if (string.IsNullOrWhiteSpace(t.GetValueOrDefault("objective"))) t["objective"] = $"Summarize the notes of project {project?.Slug}";
        return problems;
    }

    private static List<string> ValidateApplyPatch(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var project = ResolveActiveProject(t, w, problems);
        var runId = t.GetValueOrDefault("runId");
        if (string.IsNullOrWhiteSpace(runId)) problems.Add("target.runId is required.");
        else if (w.AgentRunHasOutput is not null && !w.AgentRunHasOutput(runId)) problems.Add($"Worker run {runId} has no output to apply.");
        var output = t.GetValueOrDefault("output");
        var destination = t.GetValueOrDefault("destination");
        if (string.IsNullOrWhiteSpace(output) || output.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(output)) problems.Add("target.output must be a relative path inside the run's out folder.");
        if (string.IsNullOrWhiteSpace(destination) || destination.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(destination)) problems.Add("target.destination must be a relative path inside the project.");
        else if (project is not null)
        {
            var full = Path.Combine(project.RootPath, destination);
            var check = w.Roots.Check(full);
            if (!check.Ok || !PathGuard.IsWithin(check.Canonical, PathGuard.Canonicalize(project.RootPath))) problems.Add("target.destination escapes the project folder.");
            else if (destination.StartsWith(ProjectLayout.OrchestratorDirectoryName, StringComparison.OrdinalIgnoreCase)) problems.Add(".orchestrator is Relay-owned; patches cannot target it.");
            else t["destinationPath"] = check.Canonical;
        }
        return problems;
    }

    private static List<string> ValidateExportBackup(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var path = t.GetValueOrDefault("path");
        if (string.IsNullOrWhiteSpace(path)) t["path"] = Path.Combine(w.DataRoot.BackupsDirectory, "relay-backup.zip");
        else
        {
            var canonical = PathGuard.Canonicalize(path);
            var allowed = PathGuard.IsWithin(canonical, PathGuard.Canonicalize(w.DataRoot.BackupsDirectory)) || w.Roots.Check(canonical).Ok;
            if (!allowed) problems.Add("Backups may only be written under the data root's backups folder or a registered workspace.");
            if (!canonical.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) problems.Add("Backup path must end in .zip.");
            t["path"] = canonical;
        }
        return problems;
    }

    private static List<string> ValidateMoveNote(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var from = ResolveActiveProject(t, w, problems);
        var noteId = t.GetValueOrDefault("noteId");
        if (string.IsNullOrWhiteSpace(noteId)) problems.Add("target.noteId is required.");
        else if (from is not null && !w.ProjectNoteExists(from.Id, noteId)) problems.Add($"Note {noteId} does not exist in project {from.Slug}.");
        var toKey = t.GetValueOrDefault("toProjectId") ?? t.GetValueOrDefault("toProject");
        if (string.IsNullOrWhiteSpace(toKey)) { problems.Add("target.toProjectId is required."); return problems; }
        var to = w.Registry.FindActive(toKey);
        if (to is null) problems.Add($"No active project matches '{toKey}'.");
        else
        {
            if (from is not null && to.Id == from.Id) problems.Add("The note is already in that project.");
            if (!Directory.Exists(to.RootPath)) problems.Add($"Destination project folder is missing: {to.RootPath}");
            t["toProjectId"] = to.Id;
            t["toProjectSlug"] = to.Slug;
        }
        return problems;
    }

    private static List<string> ValidateDelete(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var project = ResolveActiveProject(t, w, problems);
        if (project is not null && !Directory.Exists(project.RootPath)) problems.Add($"Project folder is missing: {project.RootPath}");
        if (project is not null)
        {
            var check = w.Roots.Check(project.RootPath);
            if (!check.Ok) problems.Add($"Project folder is outside every registered root: {check.Reason}");
        }
        if (!string.Equals(t.GetValueOrDefault("confirm"), "delete", StringComparison.Ordinal)) problems.Add("target.confirm must be the word 'delete': deletion is permanent and needs the explicit word.");
        return problems;
    }

    private static List<string> ValidateModelRequest(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var profile = t.GetValueOrDefault("profile");
        if (string.IsNullOrWhiteSpace(profile)) problems.Add("target.profile must name an external model profile.");
        else if (!w.ExternalProfiles.Contains(profile, StringComparer.Ordinal)) problems.Add($"No external model profile named '{profile}' is configured." + (w.ExternalProfiles.Count == 0 ? " Add one in Settings → External models." : $" Known: {string.Join(", ", w.ExternalProfiles)}."));
        if (string.IsNullOrWhiteSpace(t.GetValueOrDefault("objective"))) problems.Add("target.objective is required: what the external model is asked to do.");
        var refs = (t.GetValueOrDefault("refs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var r in refs)
        {
            if (w.ReferenceExists is not null && !w.ReferenceExists(r)) problems.Add($"Reference '{r}' is not something Relay holds; packages may only carry local references returned by tools.");
        }
        t["refs"] = string.Join(",", refs);
        if (!int.TryParse(t.GetValueOrDefault("budgetTokens"), out var budget) || budget <= 0) t["budgetTokens"] = "4000";
        else if (budget > 200_000) problems.Add("target.budgetTokens is unreasonably large.");
        t["allowSearch"] = string.Equals(t.GetValueOrDefault("allowSearch"), "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        return problems;
    }

    private static List<string> ValidateUpdatePreference(Dictionary<string, string> t)
    {
        var problems = new List<string>();
        var key = t.GetValueOrDefault("key");
        var value = t.GetValueOrDefault("value");
        if (string.IsNullOrWhiteSpace(key)) { problems.Add("target.key is required (response.verbosity, response.promptLine, display.alwaysShow, display.stopShowing, display.maxAlertsPer10Minutes, display.maxResultsPer5Minutes, display.cooldownSeconds, filing.grant, filing.revoke, retention.bufferSeconds, retention.excerptMaxSeconds, sources.allowOnlineSearch)."); return problems; }
        switch (key)
        {
            case "response.verbosity":
                if (!Preferences.ResponsePreferences.Verbosities.Contains(value ?? "")) problems.Add("value must be minimalist, concise, or normal.");
                break;
            case "display.alwaysShow":
            case "display.stopShowing":
                if (string.IsNullOrWhiteSpace(value)) problems.Add("value must be the term.");
                break;
            case "response.promptLine":
                if (string.IsNullOrWhiteSpace(value) || value.Length > 300) problems.Add("value must be a prompt line under 300 characters.");
                break;
            case "filing.grant":
            case "filing.revoke":
                if (string.IsNullOrWhiteSpace(value)) problems.Add("value must be the granted action (e.g. route_note).");
                else if (Actions.NeverGranted.Contains(value)) problems.Add($"'{value}' can never be covered by a standing grant.");
                else if (TierOf(value) == Tier.Prohibited) problems.Add($"'{value}' is not an action that can be granted.");
                break;
            case "sources.allowOnlineSearch":
                if (value is not ("true" or "false")) problems.Add("value must be true or false.");
                break;
            case "retention.bufferSeconds":
                if (!int.TryParse(value, out var s) || s is < 15 or > 600) problems.Add("value must be 15–600 seconds.");
                break;
            case "retention.excerptMaxSeconds":
                if (!int.TryParse(value, out var e) || e is < 5 or > 120) problems.Add("value must be 5–120 seconds.");
                break;
            case "display.maxAlertsPer10Minutes":
            case "display.maxResultsPer5Minutes":
                if (!int.TryParse(value, out var n) || n is < 0 or > 50) problems.Add("value must be a count from 0 to 50.");
                break;
            case "display.cooldownSeconds":
                if (!int.TryParse(value, out var c) || c is < 0 or > 3600) problems.Add("value must be 0–3600 seconds.");
                break;
            default:
                problems.Add($"'{key}' is not a preference Relay knows.");
                break;
        }
        return problems;
    }

    private static List<string> ValidateUpdatePrompt(Dictionary<string, string> t, PolicyWorld w)
    {
        var problems = new List<string>();
        var name = t.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(name) || !w.PromptFragments.Contains(name, StringComparer.Ordinal)) problems.Add($"target.name must be one of the prompt fragments: {string.Join(", ", w.PromptFragments)}.");
        var content = t.GetValueOrDefault("content");
        if (string.IsNullOrWhiteSpace(content)) problems.Add("target.content is required.");
        else if (content.Length > 2000) problems.Add("target.content is longer than 2000 characters.");
        return problems;
    }

    private static ProjectRecord? ResolveActiveProject(Dictionary<string, string> t, PolicyWorld w, List<string> problems)
    {
        var key = t.GetValueOrDefault("projectId") ?? t.GetValueOrDefault("project");
        if (string.IsNullOrWhiteSpace(key)) { problems.Add("target.projectId (or target.project) is required."); return null; }
        var project = w.Registry.FindActive(key);
        if (project is null) { problems.Add($"No active project matches '{key}'."); return null; }
        t["projectId"] = project.Id;
        t["projectSlug"] = project.Slug;
        return project;
    }
}
