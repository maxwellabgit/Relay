using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Relay.Core.Ids;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Search;

namespace Relay.Core.Orchestration;

/// <summary>
/// A deterministic orchestrator: a small command grammar that covers project management, filing
/// notes, recall, summaries, and backups without any model. It is the default so Relay works
/// offline, and it is the reference implementation the scenario tests exercise. Anything it does
/// not understand is returned as <c>Understood=false</c> for a model to take over.
/// </summary>
public sealed partial class RuleBasedOrchestrator : IOrchestrator
{
    public string Name => "rules";

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^(?:please\s+)?(?:create|make|start|add|set\s+up|open)\s+(?:a\s+|the\s+)?(?:new\s+)?project\s+(?:called\s+|named\s+|for\s+)?[""“']?(?<name>.+?)[""”']?$", Opts)] private static partial Regex CreateProject();
    [GeneratedRegex(@"^(?:please\s+)?(?:permanently\s+)?(?:delete|erase|wipe|purge)\s+(?:the\s+)?project\s+[""“']?(?<name>.+?)[""”']?(?:\s+(?:permanently|forever|for\s+good))?$", Opts)] private static partial Regex HardDelete();
    [GeneratedRegex(@"^(?:please\s+)?(?:archive|close|retire|remove)\s+(?:the\s+)?project\s+[""“']?(?<name>.+?)[""”']?$", Opts)] private static partial Regex ArchiveProject();
    [GeneratedRegex(@"^(?:please\s+)?(?:restore|unarchive|reopen|bring\s+back)\s+(?:the\s+)?project\s+[""“']?(?<name>.+?)[""”']?$", Opts)] private static partial Regex RestoreProject();
    [GeneratedRegex(@"^(?:please\s+)?rename\s+(?:the\s+)?project\s+[""“']?(?<old>.+?)[""”']?\s+to\s+[""“']?(?<new>.+?)[""”']?$", Opts)] private static partial Regex RenameProject();
    [GeneratedRegex(@"^(?:please\s+)?(?:list|show|show\s+me|what\s+are)\s+(?:me\s+)?(?:all\s+)?(?:of\s+)?(?:my\s+|the\s+)?(?:active\s+)?projects\??$|^what\s+projects\s+(?:do\s+i\s+have|are\s+there|exist)\??$", Opts)] private static partial Regex ListProjects();
    [GeneratedRegex(@"^(?:please\s+)?(?:file|put|move|route|save|add)\s+(?<which>all|this|that|the\s+last|the\s+latest|my\s+last|the|my)\s+(?:draft\s+)?notes?\s+(?:in|into|under|to)\s+(?:the\s+)?(?:project\s+)?[""“']?(?<name>.+?)[""”']?$", Opts)] private static partial Regex FileNote();
    [GeneratedRegex(@"^(?:please\s+)?(?:remember|note|jot\s+down|write\s+down|record|keep\s+in\s+mind)(?:\s+that|\s+this)?[:,]?\s+(?<text>.+)$", Opts)] private static partial Regex Remember();
    [GeneratedRegex(@"^(?:please\s+)?(?:summari[sz]e|write\s+a\s+summary\s+of|sum\s+up)\s+(?:the\s+)?(?:notes\s+(?:in|of|for|from)\s+)?(?:the\s+)?(?:project\s+)?[""“']?(?<name>.+?)[""”']?(?:'s\s+notes|\s+notes)?$", Opts)] private static partial Regex Summarize();
    [GeneratedRegex(@"^(?:please\s+)?(?:apply|accept|keep|save|file)\s+(?:the\s+)?(?:worker(?:'s)?\s+|latest\s+|new\s+)?(?:summary|output|result|report)(?:\s+(?:from|of)\s+the\s+worker)?\s+(?:to|for|into|under|in)\s+(?:the\s+)?(?:project\s+)?[""“']?(?<name>.+?)[""”']?$", Opts)] private static partial Regex ApplyOutput();
    [GeneratedRegex(@"^(?:please\s+)?(?:export|create|make|run|take)\s+(?:a\s+)?(?:full\s+)?backup(?:\s+now)?$|^back\s?up\s+(?:everything|relay|my\s+data|now)$", Opts)] private static partial Regex Backup();
    [GeneratedRegex(@"^(?:please\s+)?(?:what(?:'s|\s+is)\s+in|show\s+(?:me\s+)?(?:the\s+)?notes\s+(?:in|of|for)|list\s+(?:the\s+)?notes\s+(?:in|of|for))\s+(?:the\s+)?(?:project\s+)?[""“']?(?<name>.+?)[""”']?\??$", Opts)] private static partial Regex ProjectNotes();
    [GeneratedRegex(@"^(?:please\s+)?(?:what\s+did\s+i\s+say\s+about|what\s+do\s+(?:i|we)\s+know\s+about|what\s+have\s+i\s+(?:noted|said|written)\s+about|recall|find|search(?:\s+for)?|look\s+up|remind\s+me\s+about|show\s+me\s+(?:notes|everything)\s+about|anything\s+about|do\s+i\s+have\s+(?:any\s+)?notes\s+(?:about|on))\s+(?<q>.+?)\??$", Opts)] private static partial Regex Recall();
    [GeneratedRegex(@"^(?:what|when|where|who|why|how|did|do|does|is|are|have|has|was|were|which|can|could|should)\b", Opts)] private static partial Regex Question();

    public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
    {
        var text = Normalize(request.Instruction);
        var steps = new List<string> { "Interpret the instruction with the built-in grammar" };
        Match m;

        if ((m = HardDelete().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var project = context.Registry.FindActive(name);
            if (project is null) return Done(Unknown(steps, $"Delete project '{name}'", name, context));
            if (request.Origin != Tasks.TaskOrigin.Direct) return Done(Answer(steps, $"Delete project '{project.Name}'", "Deletion is only proposed from a direct request, never from something overheard."));
            steps.Add("Deletion is permanent: propose delete_project (requires approval; archive_project is the recoverable alternative)");
            return Done(new TurnPlan(true, $"Delete project '{project.Name}'", steps, null, [],
                [Propose(request, Actions.DeleteProject, "Instruction asked to delete the project. A safety zip is written to the backups folder first; the folder is then removed for good.", new() { ["projectId"] = project.Id, ["confirm"] = "delete" },
                    [$"Remove {project.RootPath} and every note in it", "Registry status becomes deleted; not restorable"], Risks.ControlledWrite, true)], Name));
        }
        if ((m = CreateProject().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var slug = Slug.From(name);
            steps.Add($"Derive slug '{slug}' and check the registry");
            var existing = context.Registry.FindActive(name);
            if (existing is not null)
            {
                return Done(Answer(steps, $"Create project '{name}'", $"A project called '{existing.Name}' ({existing.Slug}) already exists at {existing.RootPath}. Nothing was proposed."));
            }
            steps.Add("Propose create_project (requires approval)");
            return Done(new TurnPlan(true, $"Create project '{name}'", steps, null, [],
                [Propose(request, Actions.CreateProject, $"Instruction asked to create a project named '{name}'.", new() { ["name"] = name, ["slug"] = slug },
                    [$"New folder '{slug}' with the standard layout in the first registered workspace", "Registry entry and project.created record"], Risks.ControlledWrite, true)], Name));
        }
        if ((m = RestoreProject().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var project = context.Registry.Find(name);
            if (project is null) return Done(Unknown(steps, $"Restore project '{name}'", name, context));
            if (project.IsActive) return Done(Answer(steps, $"Restore project '{name}'", $"'{project.Name}' is active, not archived."));
            steps.Add("Propose restore_project (requires approval)");
            return Done(new TurnPlan(true, $"Restore project '{project.Name}'", steps, null, [],
                [Propose(request, Actions.RestoreProject, "Instruction asked to bring an archived project back.", new() { ["projectId"] = project.Id },
                    [$"Move {project.ArchivedPath} back to {project.RootPath}", "Verify the archive manifest and report any differences"], Risks.ControlledWrite, true)], Name));
        }
        if ((m = ArchiveProject().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var project = context.Registry.FindActive(name);
            if (project is null) return Done(Unknown(steps, $"Archive project '{name}'", name, context));
            steps.Add("Propose archive_project (requires approval)");
            return Done(new TurnPlan(true, $"Archive project '{project.Name}'", steps, null, [],
                [Propose(request, Actions.ArchiveProject, "Instruction asked to archive/remove the project; the folder moves to the recoverable archive.", new() { ["projectId"] = project.Id },
                    [$"Move {project.RootPath} into the archive with a manifest of every file hash", "Registry status becomes archived; restorable for 90 days"], Risks.ControlledWrite, true)], Name));
        }
        if ((m = RenameProject().Match(text)).Success)
        {
            var oldName = Clean(m.Groups["old"].Value);
            var newName = Clean(m.Groups["new"].Value);
            var project = context.Registry.FindActive(oldName);
            if (project is null) return Done(Unknown(steps, $"Rename project '{oldName}'", oldName, context));
            steps.Add("Propose rename_project (requires approval)");
            return Done(new TurnPlan(true, $"Rename project '{project.Name}' to '{newName}'", steps, null, [],
                [Propose(request, Actions.RenameProject, "Instruction asked to rename the project.", new() { ["projectId"] = project.Id, ["newName"] = newName, ["newSlug"] = Slug.From(newName) },
                    [$"Folder and slug become '{Slug.From(newName)}'; the old name is kept as an alias so recall still works"], Risks.ControlledWrite, true)], Name));
        }
        if (ListProjects().IsMatch(text))
        {
            steps.Add("Call list_projects");
            var result = context.Tools.Call("list_projects", new Dictionary<string, string>());
            var all = context.Registry.All;
            var sb = new StringBuilder();
            if (all.Count == 0) sb.Append("No projects yet. Say \"create project <name>\" to start one.");
            else
            {
                sb.Append(all.Count).Append(" project(s):\n");
                foreach (var p in all) sb.Append("• ").Append(p.Name).Append(" (").Append(p.Slug).Append(", ").Append(p.Status).Append(") — ").Append(p.RootPath).Append('\n');
            }
            return Done(new TurnPlan(true, "List projects", steps, sb.ToString().TrimEnd(), [], [], Name, result.Ok ? null : result.Error));
        }
        if ((m = ProjectNotes().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var project = context.Registry.Find(name);
            if (project is null) return Done(Unknown(steps, $"Show notes in '{name}'", name, context));
            steps.Add($"Call project_notes for {project.Slug}");
            var result = context.Tools.Call("project_notes", new Dictionary<string, string> { ["projectId"] = project.Id });
            if (!result.Ok) return Done(Answer(steps, $"Show notes in '{project.Name}'", "Could not read the project: " + result.Error));
            var notes = (result.Data as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? [];
            var sb = new StringBuilder().Append(result.Summary).Append('\n');
            foreach (var n in notes)
            {
                var t = n.GetType();
                sb.Append("• [").Append(t.GetProperty("type")?.GetValue(n)).Append('/').Append(t.GetProperty("status")?.GetValue(n)).Append("] ")
                  .Append(t.GetProperty("firstLine")?.GetValue(n)).Append("  (").Append(t.GetProperty("id")?.GetValue(n)).Append(")\n");
            }
            return Done(new TurnPlan(true, $"Show notes in '{project.Name}'", steps, sb.ToString().TrimEnd(), [], [], Name));
        }
        if ((m = FileNote().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var which = m.Groups["which"].Value.ToLowerInvariant();
            var project = context.Registry.FindActive(name);
            if (project is null) return Done(Unknown(steps, $"File note under '{name}'", name, context));
            steps.Add("Call list_draft_notes");
            context.Tools.Call("list_draft_notes", new Dictionary<string, string>());
            var drafts = context.Drafts.Unrouted();
            if (drafts.Count == 0) return Done(Answer(steps, $"File note under '{project.Name}'", "There are no draft notes in staging to file."));
            var chosen = which == "all" ? drafts.ToList() : [drafts[^1]];
            steps.Add($"Propose route_note for {chosen.Count} draft(s) (automatic: adds new notes, modifies nothing)");
            var proposals = chosen.Select(d => Propose(request, Actions.RouteNote, $"Instruction asked to file the draft note under '{project.Name}'.",
                new() { ["noteId"] = d.NoteId, ["projectId"] = project.Id, ["confidence"] = "1" }, [$"New note file in {project.Slug}/notes; staging copy marked routed"], Risks.ControlledWrite, false)).ToList();
            return Done(new TurnPlan(true, $"File {chosen.Count} draft note(s) under '{project.Name}'", steps, null, [], proposals, Name));
        }
        if ((m = Remember().Match(text)).Success)
        {
            var body = Clean(m.Groups["text"].Value);
            steps.Add("Propose create_draft_note in staging (automatic)");
            var type = body.EndsWith('?') ? NoteTypes.Question : NoteTypes.Idea;
            // The span points at the remembered words inside the stored instruction; if normalization moved them, cite the whole capture.
            var start = request.Instruction.IndexOf(body, StringComparison.Ordinal);
            var (spanStart, spanEnd) = start >= 0 ? (start, start + body.Length) : (0, request.Instruction.Length);
            return Done(new TurnPlan(true, "Save a draft note", steps, null, [],
                [Propose(request, Actions.CreateDraftNote, "Instruction asked Relay to remember something.",
                    new() { ["text"] = body, ["type"] = type, ["captureId"] = request.CaptureId, ["spanStart"] = spanStart.ToString(CultureInfo.InvariantCulture), ["spanEnd"] = spanEnd.ToString(CultureInfo.InvariantCulture) },
                    ["Draft note in staging; routing decided by the router or a later instruction"], Risks.StagingWrite, false)], Name));
        }
        if ((m = Summarize().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var project = context.Registry.FindActive(name);
            if (project is null) return Done(Unknown(steps, $"Summarize '{name}'", name, context));
            steps.Add("Propose launch_worker summarize (requires approval; sandboxed, staging output only)");
            return Done(new TurnPlan(true, $"Summarize project '{project.Name}' with a worker", steps, null, [],
                [Propose(request, Actions.LaunchWorker, "A summary is derived content; a sandboxed worker writes it to staging and a second approval applies it.",
                    new() { ["projectId"] = project.Id, ["task"] = "summarize", ["objective"] = $"Summarize the notes of project {project.Slug}", ["network"] = "false" },
                    ["Worker process reads the project's notes through the broker (read-only)", "Writes summary.md to the run's staging out folder", "You then decide whether to apply it as artifacts/summary.md"], Risks.ControlledWrite, true)], Name));
        }
        if ((m = ApplyOutput().Match(text)).Success)
        {
            var name = Clean(m.Groups["name"].Value);
            var project = context.Registry.FindActive(name);
            if (project is null) return Done(Unknown(steps, $"Apply worker output to '{name}'", name, context));
            steps.Add($"Look for completed, unapplied worker runs for {project.Slug}");
            var run = context.CompletedRuns(project.Id).FirstOrDefault();
            if (run is null) return Done(Answer(steps, $"Apply worker output to '{project.Name}'", $"No completed worker output is waiting for '{project.Name}'. Say \"summarize {project.Slug}\" to produce one."));
            var output = run.Outputs.FirstOrDefault(o => o.StartsWith("out/", StringComparison.Ordinal))?[4..] ?? "summary.md";
            var destination = "artifacts/" + output;
            steps.Add($"Propose apply_patch for run {run.RunId} → {destination} (requires approval)");
            return Done(new TurnPlan(true, $"Apply worker output to '{project.Name}'", steps, null, [],
                [Propose(request, Actions.ApplyPatch, $"Worker run {run.RunId} completed ({run.Summary}); applying its output is a separate decision.",
                    new() { ["projectId"] = project.Id, ["runId"] = run.RunId, ["output"] = output, ["destination"] = destination },
                    [$"Write {destination} in {project.Slug} from the run's staging out folder", "An existing file at that path is versioned first, never overwritten in place"], Risks.ControlledWrite, true)], Name));
        }
        if (Backup().IsMatch(text))
        {
            steps.Add("Propose export_backup (requires approval)");
            return Done(new TurnPlan(true, "Export a verified backup", steps, null, [],
                [Propose(request, Actions.ExportBackup, "Instruction asked for a backup.", new(), ["Zip of the data root and every active project with a hashed manifest, then verified"], Risks.ControlledWrite, true)], Name));
        }
        if ((m = Recall().Match(text)).Success || Question().IsMatch(text))
        {
            var query = m.Success ? Clean(m.Groups["q"].Value) : text.TrimEnd('?');
            steps.Add($"Call search for \"{query}\"");
            var result = context.Tools.Call("search", new Dictionary<string, string> { ["query"] = query, ["limit"] = "6", ["exclude"] = request.CaptureId });
            var hits = result.Hits ?? [];
            var (answer, citations) = RenderRecall(query, hits, context.Registry);
            steps.Add(hits.Count == 0 ? "Nothing matched" : $"Cite {hits.Count} passage(s) with their ledger spans");
            // A question-shaped instruction that matched nothing is not something the grammar understood; it keeps
            // its honest answer for rules-only mode, but a model orchestrator, when present, gets the turn.
            var understood = m.Success || hits.Count > 0;
            return Done(new TurnPlan(understood, $"Recall: {query}", steps, answer, citations, [], Name));
        }

        return Done(TurnPlan.NotUnderstood(Name, "Instruction not covered by the built-in grammar"));
    }

    private static Task<TurnPlan> Done(TurnPlan plan) => Task.FromResult(plan);

    private static TurnPlan Answer(List<string> steps, string summary, string answer) => new(true, summary, steps, answer, [], [], "rules");

    private static TurnPlan Unknown(List<string> steps, string summary, string name, TurnContext context)
    {
        var known = string.Join(", ", context.Registry.Active.Select(p => $"'{p.Name}' ({p.Slug})"));
        steps.Add("No active project matched the name");
        return Answer(steps, summary, $"No active project matches '{name}'." + (known.Length > 0 ? $" Active projects: {known}." : " There are no active projects yet."));
    }

    internal static Proposal Propose(TurnRequest request, string action, string reason, Dictionary<string, string> target, string[] effects, string risk, bool requiresApproval)
        => new(Ulid.NewUlid(request.At), action, reason, target, [request.SourceEventId], effects, risk, requiresApproval, Producers.Rules);

    internal static (string Answer, List<Citation> Citations) RenderRecall(string query, IReadOnlyList<SearchHit> hits, ProjectRegistry registry)
    {
        var citations = new List<Citation>();
        if (hits.Count == 0) return ($"Nothing stored mentions \"{query}\".", citations);
        var sb = new StringBuilder();
        sb.Append("Found ").Append(hits.Count).Append(" passage(s) about \"").Append(query).Append("\":\n");
        var n = 1;
        foreach (var h in hits)
        {
            var where = h.Kind switch
            {
                SearchIndex.NoteKind => $"{h.ProjectSlug}/{NoteTypes.Folder(h.Type)}/{h.Id}",
                SearchIndex.DraftKind => $"staging draft {h.Id}",
                _ => $"{h.Type} capture {h.Id}",
            };
            var marker = h.Status switch { NoteStatus.Superseded => " (superseded)", NoteStatus.Disputed => " (disputed)", _ => "" };
            sb.Append(n++).Append(". [").Append(where).Append(marker).Append(", ").Append(h.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("] ").Append(h.Excerpt).Append('\n');
            citations.Add(new Citation(h.Kind, h.Id, h.ProjectId, h.ProjectSlug, h.Excerpt, h.Span));
        }
        return (sb.ToString().TrimEnd(), citations);
    }

    private static string Normalize(string instruction)
    {
        var t = instruction.Trim();
        t = Regex.Replace(t, @"\s+", " ");
        t = t.TrimEnd('.', '!', ' ');
        return t;
    }

    private static string Clean(string value) => value.Trim().Trim('"', '“', '”', '\'', '.', ',', ';', ':').Trim();
}
