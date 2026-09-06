using System.Text.RegularExpressions;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Preferences;

namespace Relay.Core.Orchestration;

/// <summary>
/// The part of the grammar that shapes Relay itself: response style, pinned definitions, standing
/// filing grants and their revocation. Every match becomes an <c>update_preference</c> (and, for
/// style, an <c>update_prompt</c>) proposal: typed, approved by the user, applied as a reversible
/// change set. Nothing here reaches a prompt as prose; the preference store compiles it.
/// </summary>
public sealed partial class RuleBasedOrchestrator
{
    private const string TypeWords = @"(?<type>decisions?|tasks?|ideas?|questions?|facts?|references?|notes?)";
    private const string Automatically = @"(?:automatically|without\s+asking(?:\s+me)?(?:\s+first)?|on\s+your\s+own|by\s+yourself|unprompted)";

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:update|change|set|adjust)\s+(?:our|my|the|your)\s+(?:(?:response|answer|reply)\s+)?(?:preferences?|settings?|style|defaults?|verbosity|length)\s+(?:to|so\s+that\s+you|and)\s+)?(?:from\s+now\s+on,?\s+)?(?:always\s+)?(?:keep|make|give|display|show|use|be|answer|respond|reply|write|prefer|stay)?\s*(?:me\s+)?(?:it\s+|them\s+)?(?:your\s+|the\s+|all\s+|only\s+)?(?:responses?|answers?|replies|text|output)?\s*(?:should\s+be\s+|to\s+be\s+|in\s+|as\s+|with\s+)?(?:more\s+|very\s+|really\s+)?(?<style>concise(?:ly)?|brief(?:ly)?|short(?:er)?|terse(?:ly)?|succinct(?:ly)?|compact|minimal(?:ist|ly)?|one[- ]liners?|normal(?:ly)?|full(?:y)?|complete(?:ly)?|detailed|longer|in\s+detail)(?:\s+(?:text|responses?|answers?|replies|mode|style|detail))?(?:\s+from\s+now\s+on)?$", Opts)]
    private static partial Regex ResponseStyle();

    [GeneratedRegex(@"^(?:please\s+)?(?:always\s+(?:show|display|pin|tell\s+me)|pin|keep\s+(?:showing|displaying))\s+(?:me\s+)?(?:what\s+|the\s+(?:definition|meaning|expansion)\s+of\s+)?[""“']?(?<term>[^""”']+?)[""”']?(?:\s+(?:means|stands\s+for|is))?(?:\s+when(?:ever)?\s+(?:it|they|someone)\s+(?:comes?\s+up|says?\s+it|mentions?\s+it))?$", Opts)]
    private static partial Regex AlwaysShow();

    [GeneratedRegex(@"^(?:please\s+)?(?:stop\s+(?:showing|displaying|pinning)|unpin|don'?t\s+(?:show|display|pin))\s+(?:me\s+)?(?:what\s+|the\s+(?:definition|meaning)\s+of\s+)?[""“']?(?<term>[^""”']+?)[""”']?(?:\s+(?:means|stands\s+for|is))?(?:\s+any\s?more)?$", Opts)]
    private static partial Regex StopShowing();

    [GeneratedRegex(@"^(?:please\s+)?(?:file|save|record|keep|route|store)\s+(?:the\s+|all\s+|new\s+|every\s+|any\s+)?(?<project>.+?)(?:'s)?\s+" + TypeWords + @"\s+" + Automatically + @"$", Opts)]
    private static partial Regex GrantFilingA();

    [GeneratedRegex(@"^(?:please\s+)?(?:file|save|record|keep|route|store)\s+(?:the\s+|all\s+|new\s+|every\s+|any\s+)?" + TypeWords + @"\s+(?:under|in|into|for|to)\s+(?:the\s+)?(?:project\s+)?[""“']?(?<project>.+?)[""”']?\s+" + Automatically + @"$", Opts)]
    private static partial Regex GrantFilingB();

    [GeneratedRegex(@"^(?:please\s+)?(?:stop\s+filing|don'?t\s+file|ask\s+(?:me\s+)?(?:first\s+)?before\s+filing|revoke\s+(?:the\s+)?(?:grant|permission)\s+(?:to\s+file|for))\s+(?:the\s+|all\s+|new\s+|every\s+|any\s+)?(?<project>.+?)(?:'s)?\s+" + TypeWords + @"(?:\s+" + Automatically + @")?$", Opts)]
    private static partial Regex RevokeFilingA();

    [GeneratedRegex(@"^(?:please\s+)?(?:stop\s+filing|don'?t\s+file|ask\s+(?:me\s+)?(?:first\s+)?before\s+filing)\s+(?:the\s+|all\s+|new\s+|every\s+|any\s+)?" + TypeWords + @"\s+(?:under|in|into|for|to)\s+(?:the\s+)?(?:project\s+)?[""“']?(?<project>.+?)[""”']?(?:\s+" + Automatically + @")?$", Opts)]
    private static partial Regex RevokeFilingB();

    [GeneratedRegex(@"^(?:please\s+)?(?:(?<allow>allow|enable|permit|turn\s+on|you\s+(?:may|can))|(?<deny>disallow|disable|forbid|turn\s+off|stop|never|don'?t|do\s+not|you\s+may\s+not))\s+(?:use\s+|using\s+|do\s+|doing\s+)?(?:the\s+)?(?:online|web|internet)\s+search(?:es|ing)?(?:\s+(?:for\s+external\s+tasks|from\s+now\s+on|any\s?more))?$|^(?:please\s+)?(?<deny2>stop|never|don'?t|do\s+not)\s+search(?:ing)?\s+(?:the\s+)?(?:online|web|internet)(?:\s+any\s?more)?$", Opts)]
    private static partial Regex OnlineSearch();

    /// <summary>Tries the self-shaping grammar. Null when the instruction is not about Relay's own behaviour.</summary>
    private static TurnPlan? TryShape(string text, TurnRequest request, TurnContext context, List<string> steps)
    {
        Match m;
        if (request.Origin != Tasks.TaskOrigin.Direct) return null;   // preferences change only on a direct request; policy enforces the same

        if ((m = OnlineSearch().Match(text)).Success)
        {
            var allow = m.Groups["allow"].Success;
            var current = context.Preferences?.AllowOnlineSearch ?? false;
            if (allow == current) return Answer(steps, allow ? "Allow online search" : "Disallow online search", allow ? "Online search is already allowed for external tasks." : "Online search is already off; each external task that needs it asks you.");
            steps.Add($"Propose sources.allowOnlineSearch = {(allow ? "true" : "false")} (requires approval; revertible)");
            return new TurnPlan(true, allow ? "Allow online search" : "Disallow online search", steps, null, [],
                [Propose(request, Actions.UpdatePreference, allow ? "Instruction allowed external tasks to search online." : "Instruction withdrew the standing permission to search online.",
                    Contract(new() { ["key"] = "sources.allowOnlineSearch", ["value"] = allow ? "true" : "false" },
                        allow ? "External tasks that need current information can search without a per-task note" : "No external task searches online unless you approve that task",
                        "Writes config\\preferences.json (change set)", "One typed preference: sources.allowOnlineSearch",
                        allow ? "The next model.request proposal with search reads 'allowed by your sources preference'" : "The next model.request proposal with search reads 'not granted by preference'"),
                    [allow ? "External tasks approved with search on no longer need a per-task note; each still shows what leaves the machine" : "Every external task that wants to search online says so in its proposal and needs your approval for that task"], Risks.ControlledWrite, true)], "rules");
        }
        if ((m = ResponseStyle().Match(text)).Success)
        {
            var verbosity = StyleOf(m.Groups["style"].Value);
            var current = context.Preferences?.PromptFragment;
            steps.Add($"Response style requested: {verbosity}");
            var line = PromptLine(request.Instruction);
            var proposals = new List<Proposal>
            {
                Propose(request, Actions.UpdatePreference, $"Instruction asked for {verbosity} responses. The typed preference sets the answer length limits and the style line RELAY0 is given.",
                    Contract(new() { ["key"] = "response.verbosity", ["value"] = verbosity },
                        $"Answers are {verbosity}: shorter to read, fewer completion tokens per task", "Writes config\\preferences.json (change set)",
                        "One typed preference: response.verbosity; answer limits and the style line derive from it", $"Compiled preferences report verbosity '{verbosity}' and the next answer stays within its character limit"),
                    [$"response.verbosity = {verbosity} (change set, revertible)", "Answer length limits and the response-style prompt line follow from it"], Risks.ControlledWrite, true),
            };
            var fragment = ComposeFragment(context.PromptFragment, line);
            if (fragment is not null)
            {
                steps.Add("Also propose the instruction, in your words, as a line of the planner prompt fragment");
                proposals.Add(Propose(request, Actions.UpdatePrompt, "Your own wording is kept as an approved planner instruction so the model follows it verbatim; it is a change set with the previous text stored.",
                    Contract(new() { ["name"] = "planner", ["content"] = fragment },
                        "RELAY0's planner is told the style in your own words, so model answers follow it verbatim", "Writes config\\prompts\\planner.md (change set)",
                        $"One added line: \"{line}\"", "The planner system prompt contains the line; reverting the change set removes it"),
                    [$"config\\prompts\\planner.md gains the line \"{line}\" (change set, revertible)"], Risks.ControlledWrite, true));
            }
            return new TurnPlan(true, $"Make responses {verbosity}", steps, current is null ? null : $"Current style: {Truncate(current, 120)}", [], proposals, "rules");
        }
        if ((m = AlwaysShow().Match(text)).Success)
        {
            var term = Clean(m.Groups["term"].Value);
            if (term.Length is 0 or > 80) return null;
            if (context.Preferences?.WatchedTerms.Contains(term, StringComparer.OrdinalIgnoreCase) == true)
                return Answer(steps, $"Always show '{term}'", $"'{term}' is already pinned: its definition is refreshed in place whenever it comes up.");
            steps.Add($"Propose display.alwaysShow = '{term}' (requires approval; revertible)");
            return new TurnPlan(true, $"Always show '{term}'", steps, null, [],
                [Propose(request, Actions.UpdatePreference, $"Instruction asked to always show what '{term}' means.",
                    Contract(new() { ["key"] = "display.alwaysShow", ["value"] = term },
                        $"'{term}' is defined on screen the moment it comes up, without asking", "Writes config\\preferences.json (change set); listening reads local sources only",
                        "One watched term; the pinned card refreshes in place and bypasses the result budget", $"Hearing '{term}' while listening shows a pinned result within one judge pass"),
                    [$"'{term}' becomes a watched term: resolved as soon as it is heard, shown as a pinned card, refreshed in place"], Risks.ControlledWrite, true)], "rules");
        }
        if ((m = StopShowing().Match(text)).Success)
        {
            var term = Clean(m.Groups["term"].Value);
            if (term.Length is 0 or > 80) return null;
            if (context.Preferences?.WatchedTerms.Contains(term, StringComparer.OrdinalIgnoreCase) != true)
                return Answer(steps, $"Stop showing '{term}'", $"'{term}' is not pinned. Pinned terms: {(context.Preferences is { WatchedTerms.Count: > 0 } p ? string.Join(", ", p.WatchedTerms) : "none")}.");
            steps.Add($"Propose display.stopShowing = '{term}'");
            return new TurnPlan(true, $"Stop showing '{term}'", steps, null, [],
                [Propose(request, Actions.UpdatePreference, $"Instruction asked to stop pinning '{term}'.",
                    Contract(new() { ["key"] = "display.stopShowing", ["value"] = term },
                        $"No more pinned card for '{term}'; one less thing on screen", "Writes config\\preferences.json (change set)",
                        "Removes one watched term", $"Hearing '{term}' no longer produces a pinned result"),
                    [$"'{term}' is no longer a watched term"], Risks.ControlledWrite, true)], "rules");
        }
        if ((m = GrantFilingA().Match(text)).Success || (m = GrantFilingB().Match(text)).Success)
        {
            var name = Clean(m.Groups["project"].Value);
            var type = TypeOf(m.Groups["type"].Value);
            var project = context.Registry.FindActive(name);
            if (project is null) return Unknown(steps, $"File {name} {TypeLabel(type)} automatically", name, context);
            if (context.Preferences?.Grants.Any(g => g.Action == Actions.RouteNote && g.ProjectId == project.Id && (g.NoteType is null || g.NoteType == type)) == true)
                return Answer(steps, $"File {project.Name} {TypeLabel(type)} automatically", $"{project.Name} {TypeLabel(type)} are already filed without asking.");
            steps.Add($"Propose a standing grant: route_note for {project.Slug}" + (type is null ? "" : $" ({type} notes)") + " (requires approval; revocable)");
            var target = Contract(new Dictionary<string, string> { ["key"] = "filing.grant", ["value"] = Actions.RouteNote, ["action"] = Actions.RouteNote, ["projectId"] = project.Id },
                $"{project.Name} {TypeLabel(type)} stop waiting in Review; fewer approvals for a repeatable filing", $"Standing approval for route_note into {project.Slug}" + (type is null ? "" : $" ({type} notes)") + "; additive writes only",
                "One standing grant recorded in preferences; revocable in one step", $"A {type ?? "note"} routed to {project.Slug} with moderate confidence is filed and the ledger shows grant.applied");
            if (type is not null) target["noteType"] = type;
            return new TurnPlan(true, $"File {project.Name} {TypeLabel(type)} without asking", steps, null, [],
                [Propose(request, Actions.UpdatePreference, $"Instruction asked Relay to file {project.Name} {TypeLabel(type)} on its own.", target,
                    [$"{TypeLabelCapital(type)} routed to {project.Slug} are filed even when routing is only moderately confident, instead of waiting in Review", "The grant is recorded in preferences (change set) and can be revoked"], Risks.ControlledWrite, true)], "rules");
        }
        if ((m = RevokeFilingA().Match(text)).Success || (m = RevokeFilingB().Match(text)).Success)
        {
            var name = Clean(m.Groups["project"].Value);
            var type = TypeOf(m.Groups["type"].Value);
            var project = context.Registry.Find(name);
            if (project is null) return Unknown(steps, $"Stop filing {name} {TypeLabel(type)} automatically", name, context);
            var grants = (context.Preferences?.Grants ?? []).Where(g => g.Action == Actions.RouteNote && g.ProjectId == project.Id && (type is null || g.NoteType is null || g.NoteType == type)).ToList();
            if (grants.Count == 0) return Answer(steps, $"Stop filing {project.Name} {TypeLabel(type)} automatically", $"There is no standing grant to file {project.Name} {TypeLabel(type)}; Relay already asks.");
            steps.Add($"Propose revoking {grants.Count} standing grant(s)");
            var proposals = grants.Select(g => Propose(request, Actions.UpdatePreference, $"Instruction asked Relay to stop filing {project.Name} {TypeLabel(g.NoteType)} on its own.",
                Contract(new() { ["key"] = "filing.revoke", ["value"] = g.GrantId, ["projectId"] = project.Id },
                    $"{project.Name} {TypeLabel(g.NoteType)} wait for your decision again", "Writes config\\preferences.json (change set); removes a standing grant, adds none",
                    $"Removes grant {g.GrantId}", $"The next {g.NoteType ?? "note"} routed to {project.Slug} appears in Review instead of being filed"),
                [$"Grant {g.GrantId} ({g.Action} for {project.Slug}{(g.NoteType is null ? "" : ", " + g.NoteType + " notes")}) is removed; such notes wait for your decision again"], Risks.ControlledWrite, true)).ToList();
            return new TurnPlan(true, $"Stop filing {project.Name} {TypeLabel(type)} without asking", steps, null, [], proposals, "rules");
        }
        return null;
    }

    /// <summary>
    /// The improvement contract every self-change carries: the concrete benefit, the permissions it needs,
    /// the implementation scope, and how to tell it worked. Policy requires all four on an improve task.
    /// </summary>
    private static Dictionary<string, string> Contract(Dictionary<string, string> target, string benefit, string permissions, string scope, string acceptance)
    {
        target["benefit"] = benefit;
        target["permissions"] = permissions;
        target["scope"] = scope;
        target["acceptance"] = acceptance;
        return target;
    }

    private static string StyleOf(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.StartsWith("minimal", StringComparison.Ordinal) || w.StartsWith("one", StringComparison.Ordinal)) return ResponsePreferences.Minimalist;
        if (w.StartsWith("normal", StringComparison.Ordinal) || w.StartsWith("full", StringComparison.Ordinal) || w.StartsWith("complete", StringComparison.Ordinal) || w is "detailed" or "longer" || w.StartsWith("in ", StringComparison.Ordinal)) return ResponsePreferences.Normal;
        return ResponsePreferences.Concise;
    }

    /// <summary>The user's instruction as one prompt line: leading "update our preferences to" and the like removed, first letter capitalised, a full stop at the end.</summary>
    public static string PromptLine(string instruction)
    {
        var line = Normalize(instruction);
        line = Regex.Replace(line, @"^(?:please\s+)?(?:(?:update|change|set|adjust)\s+(?:our|my|the|your)\s+(?:(?:response|answer|reply)\s+)?(?:preferences?|settings?|style|defaults?|verbosity|length)\s+(?:to|so\s+that\s+you|and)\s+)?(?:from\s+now\s+on,?\s+)?", "", Opts).Trim();
        if (line.Length == 0) line = Normalize(instruction);
        line = char.ToUpperInvariant(line[0]) + line[1..];
        if (!line.EndsWith('.') && !line.EndsWith('!')) line += ".";
        return line;
    }

    /// <summary>The planner fragment with the line added; null when the line is already there or the fragment would exceed the policy limit.</summary>
    public static string? ComposeFragment(string? existing, string line)
    {
        var lines = (existing ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Contains(line, StringComparer.OrdinalIgnoreCase)) return null;
        lines.Add(line);
        var fragment = string.Join("\n", lines);
        return fragment.Length > 2000 ? null : fragment;
    }

    private static string? TypeOf(string word) => word.ToLowerInvariant().TrimEnd('s') switch
    {
        "decision" => NoteTypes.Decision,
        "task" => NoteTypes.Task,
        "idea" => NoteTypes.Idea,
        "question" => NoteTypes.Question,
        "fact" => NoteTypes.Fact,
        "reference" => NoteTypes.Reference,
        _ => null,
    };

    private static string TypeLabel(string? type) => type is null ? "notes" : type + "s";
    private static string TypeLabelCapital(string? type) => type is null ? "Notes" : char.ToUpperInvariant(type[0]) + type[1..] + "s";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
