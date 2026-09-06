using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.Tasks;

namespace Relay.Core.Orchestration;

/// <summary>
/// The part of the grammar that handles work Relay cannot do locally. A research request climbs the
/// source ladder in order — project notes and drafts, retained excerpts, stored external artifacts —
/// and states the two-axis knowledge gap that remains: what is <em>missing</em> (no local source) and
/// whether there is a <em>capability</em> gap (the local grammar and model cannot produce the deliverable).
/// A capability gap with a configured external profile becomes a <c>model.request</c> proposal that
/// binds the exact package. When the approved request comes back, the follow-up summarises the stored
/// artifact concisely, names its limits, cites it, and files the findings as a separate draft note.
/// </summary>
public sealed partial class RuleBasedOrchestrator
{
    [GeneratedRegex(@"^(?:please\s+)?(?:research|investigate|look\s+into|find\s+out\s+about|dig\s+into|do\s+(?:some\s+)?research\s+(?:on|about|into)|study)\s+(?<topic>.+?)(?:\s+and\s+(?:(?:then\s+)?(?:give|send|get)\s+me|write(?:\s+me)?|produce|draft|prepare|put\s+together|suggest|propose|recommend)\s+(?:an?\s+|the\s+|some\s+)?(?<deliverable>.+?))?$", Opts)]
    private static partial Regex Research();

    [GeneratedRegex(@"^(?:please\s+)?(?:ask|have|get|send\s+(?:this\s+)?to)\s+(?<profile>[A-Za-z][\w-]*)\s+(?:to\s+|for\s+)?(?<objective>.+)$", Opts)]
    private static partial Regex AskProfile();

    private static readonly string[] LimitCues = ["could not", "couldn't", "cannot", "can't", "unable", "not determine", "no information", "not available", "unknown", "uncertain", "not sure", "unverified", "cannot verify", "could not verify", "beyond", "out of scope", "not enough", "insufficient", "did not find", "no public", "no reliable"];

    private const int MaxPackagedSources = 6;

    /// <summary>Tries the research grammar and the external-result follow-up. Null when neither applies.</summary>
    private TurnPlan? TryResearch(string text, TurnRequest request, TurnContext context, List<string> steps)
    {
        Match m;
        if ((m = AskProfile().Match(text)).Success && context.ExternalProfiles.Contains(m.Groups["profile"].Value, StringComparer.OrdinalIgnoreCase))
        {
            var profile = context.ExternalProfiles.First(p => string.Equals(p, m.Groups["profile"].Value, StringComparison.OrdinalIgnoreCase));
            return ResearchPlan(request, context, steps, Clean(m.Groups["objective"].Value), null, profile);
        }
        if ((m = Research().Match(text)).Success)
        {
            var topic = Clean(m.Groups["topic"].Value);
            var deliverable = m.Groups["deliverable"].Success ? Clean(m.Groups["deliverable"].Value) : null;
            if (topic.Length == 0) return null;
            return ResearchPlan(request, context, steps, topic, deliverable, null);
        }
        return null;
    }

    private static TurnPlan ResearchPlan(TurnRequest request, TurnContext context, List<string> steps, string topic, string? deliverable, string? profileOverride)
    {
        var title = deliverable is null ? $"Research: {topic}" : $"Research: {topic} → {deliverable}";
        var query = Regex.Replace(topic, @"['’]s\b", "", RegexOptions.CultureInvariant);

        // Source ladder, in order. Every rung is a step so the trail shows what was consulted before anything is proposed.
        steps.Add($"Source ladder 1/3: project notes and drafts — search \"{query}\"");
        var result = context.Tools.Call("search", new Dictionary<string, string> { ["query"] = query, ["limit"] = "12", ["exclude"] = request.CaptureId });
        var hits = (result.Hits ?? []).ToList();
        var notes = hits.Where(h => h.Kind is SearchIndex.NoteKind or SearchIndex.DraftKind).ToList();
        var excerpts = hits.Where(h => h.Kind == SearchIndex.ExcerptKind).ToList();
        var artifacts = hits.Where(h => h.Kind == SearchIndex.ArtifactKind).ToList();
        steps.Add($"  {notes.Count} note(s)");
        steps.Add($"Source ladder 2/3: retained excerpts — {excerpts.Count}");
        steps.Add($"Source ladder 3/3: stored external artifacts — {artifacts.Count}");

        var known = notes.Concat(excerpts).Concat(artifacts).Select(h => h.Id).Distinct(StringComparer.Ordinal).ToList();
        var missing = new List<string> { $"current information about {topic} (no local source)" };
        if (deliverable is not null) missing.Add($"{deliverable} (nothing stored answers it)");
        var citations = notes.Concat(excerpts).Concat(artifacts).Take(MaxPackagedSources)
            .Select(h => new Citation(h.Kind, h.Id, h.ProjectId, h.ProjectSlug, h.Excerpt, h.Span)).ToList();
        var projects = notes.Select(h => h.ProjectSlug).Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder();
        sb.Append("Knowledge state\n");
        sb.Append("Known locally: ").Append(known.Count == 0 ? "nothing stored mentions it." : $"{notes.Count} note(s), {excerpts.Count} excerpt(s), {artifacts.Count} artifact(s) about \"{topic}\"" + (projects.Count > 0 ? $" in {string.Join(", ", projects)}" : "") + " (cited below).").Append('\n');
        sb.Append("Missing: ").Append(string.Join("; ", missing)).Append(".\n");
        sb.Append("Capability: research and ").Append(deliverable ?? "a written result").Append(" are beyond the built-in grammar and RELAY0's local model, which has no online access.");

        var profileName = profileOverride ?? context.SearchProfiles.FirstOrDefault() ?? context.ExternalProfiles.FirstOrDefault();
        var knowledge = new KnowledgeState(known, missing, true, $"{known.Count} local source(s); {missing.Count} gap(s); capability gap: external work needed");
        if (profileName is null)
        {
            steps.Add("No external model profile is configured: nothing can be proposed");
            sb.Append("\n\nNo external model profile is configured, so nothing is proposed. Add one under Settings → External models to allow a bounded external task.");
            return new TurnPlan(true, title, steps, sb.ToString(), citations, [], "rules", Knowledge: knowledge);
        }

        var allowSearch = context.SearchProfiles.Contains(profileName, StringComparer.Ordinal);
        var refs = notes.Concat(excerpts).Concat(artifacts).Select(h => h.Id).Distinct(StringComparer.Ordinal).Take(MaxPackagedSources).ToList();
        var packagedChars = notes.Concat(excerpts).Concat(artifacts).Take(MaxPackagedSources).Sum(h => h.Text.Length);
        var objective = deliverable is null
            ? $"Research {topic}. Use the attached notes as context for what the user already knows and cares about. Report findings with their sources, and say what you could not determine."
            : $"Research {topic} and produce {deliverable}. Use the attached notes as context for what the user already knows and cares about. Report findings with their sources, then the {deliverable} as numbered steps, and say what you could not determine.";
        steps.Add($"Capability gap: propose model.request to '{profileName}' with {refs.Count} source(s), ~{packagedChars + objective.Length} chars, online search {(allowSearch ? "on" : "off")} (requires approval)");
        sb.Append("\n\nProposed: an external task to '").Append(profileName).Append("' with exactly ").Append(refs.Count).Append(" local source(s) (~").Append(packagedChars + objective.Length).Append(" characters) and online search ").Append(allowSearch ? "on" : "off").Append(". The proposal shows what would leave the machine; nothing is sent until you approve.");
        if (allowSearch && context.Preferences is { AllowOnlineSearch: false }) sb.Append(" Online search is not granted by preference, so approving covers this task only.");

        var proposal = Propose(request, Actions.ModelRequest,
            $"Local sources cannot answer \"{topic}\"{(deliverable is null ? "" : $" or produce {deliverable}")}; a bounded external task can.",
            new()
            {
                ["profile"] = profileName,
                ["objective"] = objective,
                ["refs"] = string.Join(",", refs),
                ["budgetTokens"] = "4000",
                ["allowSearch"] = allowSearch ? "true" : "false",
            },
            [$"The objective and {refs.Count} local source(s) (~{packagedChars + objective.Length} chars) are sent to {profileName}", "The response is stored as a source artifact and summarised in a follow-up", "Nothing else leaves the machine"],
            Risks.ControlledWrite, true);
        return new TurnPlan(true, title, steps, sb.ToString(), citations, [proposal], "rules", Knowledge: knowledge);
    }

    /// <summary>The follow-up after an approved external task: a concise summary with limits, a citation, and the findings as a separate draft note.</summary>
    private static TurnPlan SummarizeArtifact(TurnRequest request, TurnContext context, List<string> steps)
    {
        var artifactId = request.ArtifactId!;
        steps.Add($"Read artifact {artifactId[^8..]}");
        var result = context.Tools.Call("read_artifact", new Dictionary<string, string> { ["artifactId"] = artifactId });
        if (!result.Ok || result.Hits is not { Count: > 0 }) return Answer(steps, "External result", "The external response could not be read: " + (result.Error ?? "artifact missing"));
        var hit = result.Hits[0];
        var text = hit.Text;
        var objective = ObjectiveOf(request.Instruction);
        // A summary, not a re-print: the full text is the stored artifact. Concise or minimalist preferences tighten it further.
        var maxChars = Math.Clamp(context.Preferences?.MaxAnswerChars ?? 700, 240, 700);

        var sentences = Sentences(text);
        var limits = sentences.Where(s => LimitCues.Any(c => s.Contains(c, StringComparison.OrdinalIgnoreCase))).Take(2).ToList();
        var body = sentences.Where(s => !limits.Contains(s)).ToList();
        var summary = TakeWithin(body, maxChars - Math.Min(200, limits.Sum(l => l.Length)));
        steps.Add($"Summarise {text.Length} chars into ≤{maxChars} ({sentences.Count} sentence(s); {limits.Count} limit(s) named)");

        var sb = new StringBuilder();
        sb.Append(summary);
        sb.Append(limits.Count > 0 ? "\n\nLimits: " + string.Join(" ", limits) : "\n\nLimits: the response did not name anything it could not determine.");
        sb.Append("\nSource: external artifact ").Append(artifactId).Append(" (").Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(" chars, stored locally).");

        steps.Add("Propose create_draft_note (reference) with the findings, separately from the summary");
        var note = Propose(request, Actions.CreateDraftNote, "Findings from an approved external task are kept as a reference note, traceable to the stored artifact.",
            new() { ["text"] = $"Findings ({Truncate(objective, 80)}): {summary}", ["type"] = NoteTypes.Reference, ["sourceArtifactId"] = artifactId },
            ["Draft reference note in staging pointing at the artifact; routing decided by the router or a later instruction"], Risks.StagingWrite, false);
        return new TurnPlan(true, "External result: " + Truncate(objective, 60), steps, sb.ToString(),
            [new Citation(SearchIndex.ArtifactKind, artifactId, null, null, hit.Excerpt, new SourceSpan(artifactId, 0, text.Length))], [note], "rules",
            Knowledge: new KnowledgeState([artifactId], limits.Count > 0 ? limits : [], false, limits.Count > 0 ? $"the external model named {limits.Count} limit(s)" : "no limits named"));
    }

    private static string ObjectiveOf(string instruction)
    {
        var m = Regex.Match(instruction, @"objective:\s*""(?<o>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["o"].Value : instruction;
    }

    private static List<string> Sentences(string text)
        => Regex.Split(text.Replace("\r\n", "\n"), @"(?<=[.!?])\s+|\n{2,}", RegexOptions.CultureInvariant)
            .Select(s => Regex.Replace(s, @"\s+", " ").Trim())
            .Where(s => s.Length > 0)
            .ToList();

    private static string TakeWithin(IReadOnlyList<string> sentences, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var s in sentences)
        {
            if (sb.Length > 0 && sb.Length + 1 + s.Length > maxChars) break;
            if (sb.Length == 0 && s.Length > maxChars) { sb.Append(s[..Math.Max(0, maxChars - 1)].TrimEnd()).Append('…'); break; }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }
        return sb.Length == 0 ? "(empty response)" : sb.ToString();
    }
}
