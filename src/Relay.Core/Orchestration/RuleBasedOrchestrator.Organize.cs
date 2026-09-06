using System.Text.RegularExpressions;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Search;

namespace Relay.Core.Orchestration;

/// <summary>
/// The part of the grammar that reorganises the record: moving the notes about a topic into another
/// project, creating that project first when it does not exist yet. The result is one task with an
/// operation graph: <c>create_project</c> as the prerequisite and one <c>move_note</c> per note that
/// depends on it. Each proposal is approved on its own; a dependent cannot run before its prerequisite,
/// and one whose prerequisite was rejected cannot be approved at all.
/// </summary>
public sealed partial class RuleBasedOrchestrator
{
    private const string Into = @"\s+(?:in|into|to|under)\s+(?:(?<fresh>a\s+new\s+project(?:\s+(?:called|named))?)\s+|(?:the\s+)?(?:project\s+)?)[""“']?(?<project>.+?)[""”']?$";

    [GeneratedRegex(@"^(?:please\s+)?(?:move|transfer|shift|relocate|refile|re-file)\s+(?:all\s+(?:of\s+)?|every\s+|any\s+|the\s+|my\s+)?(?:the\s+)?(?:(?<count>\d+)\s+)?(?<q>.+?)\s+notes?" + Into, Opts)]
    private static partial Regex MoveTopicA();

    [GeneratedRegex(@"^(?:please\s+)?(?:move|transfer|shift|relocate|refile|re-file)\s+(?:all\s+(?:of\s+)?|every\s+|any\s+)?(?:the\s+|my\s+)?(?:notes?|everything|anything|what(?:ever)?\s+(?:i|we)\s+have)\s+(?:about|on|mentioning|regarding|related\s+to)\s+[""“']?(?<q>.+?)[""”']?" + Into, Opts)]
    private static partial Regex MoveTopicB();

    [GeneratedRegex(@"^(?:please\s+)?(?:move|transfer|refile|re-file)\s+(?:the\s+)?note\s+(?<noteId>[0-9A-HJKMNP-TV-Z]{26})" + Into, Opts)]
    private static partial Regex MoveOne();

    private static readonly HashSet<string> NotTopics = new(StringComparer.OrdinalIgnoreCase)
        { "the", "my", "all", "this", "that", "these", "those", "last", "latest", "my last", "the last", "the latest", "draft", "drafts", "new", "some" };

    /// <summary>Tries the organising grammar. Null when the instruction is not a move.</summary>
    private TurnPlan? TryOrganize(string text, TurnRequest request, TurnContext context, List<string> steps)
    {
        Match m;
        if ((m = MoveOne().Match(text)).Success)
        {
            var noteId = m.Groups["noteId"].Value.ToUpperInvariant();
            var destination = Clean(m.Groups["project"].Value);
            steps.Add($"Look for note {noteId[..8]} in every active project");
            var owner = context.Registry.Active.FirstOrDefault(p => Directory.Exists(p.RootPath) && ProjectNoteStore.Find(p.RootPath, noteId) is not null);
            if (owner is null) return Answer(steps, $"Move note {noteId[..8]}", $"No project note has the id {noteId}.");
            return MovePlan(request, context, steps, $"note {noteId[..8]}", [(owner.Id, owner.Slug, noteId)], destination, m.Groups["fresh"].Success);
        }
        if ((m = MoveTopicA().Match(text)).Success || (m = MoveTopicB().Match(text)).Success)
        {
            var topic = Clean(m.Groups["q"].Value);
            if (topic.Length == 0 || NotTopics.Contains(topic)) return null; // "move the notes into X" is filing drafts, handled elsewhere
            var destination = Clean(m.Groups["project"].Value);
            steps.Add($"Search project notes for \"{topic}\"");
            var result = context.Tools.Call("search", new Dictionary<string, string> { ["query"] = topic, ["limit"] = "25", ["exclude"] = request.CaptureId });
            var notes = (result.Hits ?? [])
                .Where(h => h.Kind == SearchIndex.NoteKind && h.ProjectId is not null && h.ProjectSlug is not null)
                .Select(h => (ProjectId: h.ProjectId!, Slug: h.ProjectSlug!, NoteId: h.Id))
                .Distinct()
                .ToList();
            var drafts = (result.Hits ?? []).Count(h => h.Kind == SearchIndex.DraftKind);
            if (drafts > 0) steps.Add($"{drafts} draft(s) in the inbox also mention it; file those with \"file all notes under {destination}\" once the project exists");
            if (m.Groups["count"].Success && int.TryParse(m.Groups["count"].Value, out var count) && count > 0 && count < notes.Count) notes = notes.Take(count).ToList();
            return MovePlan(request, context, steps, $"the notes about \"{topic}\"", notes, destination, m.Groups["fresh"].Success);
        }
        return null;
    }

    private static TurnPlan MovePlan(TurnRequest request, TurnContext context, List<string> steps, string what,
        List<(string ProjectId, string Slug, string NoteId)> notes, string destination, bool saidNew)
    {
        var to = context.Registry.FindActive(destination);
        var toName = to?.Name ?? destination;
        var title = $"Move {what} into '{toName}'";
        if (to is not null) notes = notes.Where(n => n.ProjectId != to.Id).ToList();
        if (notes.Count == 0)
        {
            steps.Add("Nothing to move");
            return Answer(steps, title, to is null
                ? $"No project note matches {what}, so there is nothing to move and no reason to create '{destination}'."
                : $"Every note matching {what} is already in '{to.Name}'. Nothing to move.");
        }
        var proposals = new List<Proposal>();
        string? createId = null;
        if (to is null)
        {
            var slug = Slug.From(destination);
            var create = Propose(request, Actions.CreateProject, saidNew
                    ? $"Instruction asked for a new project named '{destination}'."
                    : $"No active project matches '{destination}'; the notes need somewhere to go, so the project is created first.",
                new() { ["name"] = destination, ["slug"] = slug },
                [$"New folder '{slug}' with the standard layout in the first registered workspace", "Registry entry and project.created record"], Risks.ControlledWrite, true);
            createId = create.ProposalId;
            proposals.Add(create);
            steps.Add($"'{destination}' does not exist: propose create_project first; each move depends on it");
        }
        else steps.Add($"Destination '{to.Name}' exists");
        var byProject = notes.GroupBy(n => n.Slug).Select(g => $"{g.Count()} from {g.Key}");
        steps.Add($"Propose move_note for {notes.Count} note(s): {string.Join(", ", byProject)}");
        foreach (var n in notes)
        {
            var move = Propose(request, Actions.MoveNote, $"The note matches {what}.",
                new() { ["projectId"] = n.ProjectId, ["noteId"] = n.NoteId, ["toProject"] = to?.Id ?? destination },
                [$"Note {n.NoteId[..8]} leaves {n.Slug}/notes and appears under {Slug.From(toName)}/notes; the ledger records the move", "Its versions travel with it; nothing is rewritten"], Risks.ControlledWrite, true);
            if (createId is not null) move = move with { DependsOn = [createId] };
            proposals.Add(move);
        }
        return new TurnPlan(true, title, steps, null, [], proposals, "rules");
    }
}
