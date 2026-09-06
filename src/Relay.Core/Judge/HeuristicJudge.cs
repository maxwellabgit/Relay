using System.Text.RegularExpressions;
using Relay.Core.Memory;
using Relay.Core.Notes;
using Relay.Core.Tasks;

namespace Relay.Core.Judge;

/// <summary>
/// The judge Relay runs when no model is configured. It is honest about what it is: cue words and
/// patterns, labeled "heuristic" wherever its findings appear. It exists so the pipeline, the UI and
/// the tests exercise the same path with or without RELAY0's model, and so the product degrades
/// visibly rather than silently. It never sees canonical files; the planner's tools do.
/// </summary>
public sealed partial class HeuristicJudge : IJudge
{
    public const string ProducerName = "heuristic";

    private static readonly HashSet<string> AcronymStopList = new(StringComparer.Ordinal)
    {
        "OK", "AM", "PM", "TV", "US", "UK", "EU", "USA", "ID", "TODO", "FYI", "ASAP", "I", "A", "IT", "AND", "THE", "NOT", "BUT", "FOR", "WE", "HE", "SHE", "YES", "NO",
    };

    [GeneratedRegex(@"\b(?<term>[A-Z][A-Z0-9]{1,5})\b", RegexOptions.CultureInvariant)] private static partial Regex Acronym();
    [GeneratedRegex(@"\b(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec)\b\.?\s*(the\s+)?\d{1,2}(st|nd|rd|th)?|\b(the\s+)?\d{1,2}(st|nd|rd|th)\b|\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow|next week|end of (the )?(month|quarter|week))\b|\b\d{4}-\d{2}-\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DateLike();
    [GeneratedRegex(@"\b(ships?|shipping|launch(es|ing)?|deadline|due|release|beta|go live|goes live|by)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ScheduleCue();
    [GeneratedRegex(@"\b(move|rename|archive|delete|remove|reorganize|reorganise|merge|split|consolidate|clean up|tidy|file (this|that|these|those|it))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex OrganizeCue();
    [GeneratedRegex(@"\b(research|look into|find out|investigate|compare (the )?competitors|competitor analysis|implementation plan|deep dive)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ResearchCue();
    [GeneratedRegex(@"\b(always (show|display)|from now on|prefer|keep (responses|answers|replies)|be (more )?(concise|brief|terse|verbose)|stop (showing|asking)|without asking|don't ask|update (our|my) preferences?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PreferenceCue();
    [GeneratedRegex(@"\b(what does|what's|what is|stands? for|meaning of|define)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DefineCue();
    [GeneratedRegex(@"^\s*(what|when|where|who|why|how|which|is|are|can|could|should|do|does|did|will|would|remind me|tell me|show me)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex QuestionStart();

    public string Name => ProducerName;

    public Task<JudgeDecision> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        var findings = new List<JudgeFinding>();
        if (request.Origin == TaskOrigin.Direct)
        {
            if (!string.IsNullOrWhiteSpace(request.DirectText)) findings.Add(ClassifyDirect(request.DirectText, request.Context));
            return Task.FromResult(new JudgeDecision(findings, Name, 0, 0, 0));
        }

        var fresh = request.Window.Where(s => request.NewSegmentIds.Contains(s.SegmentId, StringComparer.Ordinal)).ToList();
        foreach (var segment in fresh)
        {
            foreach (var finding in Observe(segment, request.Context)) findings.Add(finding);
        }
        return Task.FromResult(new JudgeDecision(findings, Name, 0, 0, 0));
    }

    private static JudgeFinding ClassifyDirect(string text, JudgeContext context)
    {
        var project = MentionedProject(text, context);
        var kind = TaskKind.Answer;
        if (PreferenceCue().IsMatch(text)) kind = TaskKind.Improve;
        else if (ResearchCue().IsMatch(text)) kind = TaskKind.Research;
        else if (OrganizeCue().IsMatch(text) && !QuestionStart().IsMatch(text)) kind = TaskKind.Organize;
        else if (DefineCue().IsMatch(text) && FirstAcronym(text) is not null) kind = TaskKind.Resolve;
        else if (NoteExtractor.Classify(text) is NoteTypes.Decision or NoteTypes.Task && !QuestionStart().IsMatch(text) && !text.TrimEnd().EndsWith('?')) kind = TaskKind.Remember;
        return new JudgeFinding(kind, 0.6, Truncate(text, 80), "direct ask", text, [], NoteExtractor.Topic(text), project, null,
            kind == TaskKind.Remember ? text : null, kind == TaskKind.Remember ? NoteExtractor.Classify(text) : null);
    }

    private static IEnumerable<JudgeFinding> Observe(StreamSegment segment, JudgeContext context)
    {
        var text = segment.Text.Trim();
        if (text.Length < 8) yield break;
        var project = MentionedProject(text, context);
        var watched = context.WatchedTerms.FirstOrDefault(t => Regex.IsMatch(text, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase));

        // Watched terms and acronyms: definitions must be available at once.
        var acronym = watched ?? FirstAcronym(text);
        if (acronym is not null)
        {
            yield return new JudgeFinding(TaskKind.Resolve, watched is not null ? 0.9 : 0.6, $"{acronym} mentioned", watched is not null ? "watched term" : "unfamiliar acronym",
                $"Define {acronym} as used here and cite where the definition comes from: \"{Truncate(text, 160)}\"", [segment.SegmentId], acronym, project,
                Presentation.Result, MergeKey: "define:" + acronym.ToLowerInvariant());
        }

        var type = NoteExtractor.Classify(text);
        var dated = DateLike().IsMatch(text) && ScheduleCue().IsMatch(text);

        // A dated statement about a known project is a claim worth checking against stored decisions.
        if (dated && project is not null)
        {
            yield return new JudgeFinding(TaskKind.Check, 0.7, $"{project}: dated statement", "date claim about a known project",
                $"Check this statement about {project} against the stored decisions and report whether it is consistent: \"{Truncate(text, 200)}\"", [segment.SegmentId],
                NoteExtractor.Topic(text), project, null, MergeKey: $"check:{project.ToLowerInvariant()}:{DateLike().Match(text).Value.ToLowerInvariant()}");
            if (type != NoteTypes.Decision) yield break;
        }

        if (type is NoteTypes.Decision or NoteTypes.Task && text.Split(' ').Length >= 4 && !text.EndsWith('?'))
        {
            yield return new JudgeFinding(TaskKind.Remember, project is not null ? 0.8 : 0.6, $"{type}: {Truncate(text, 60)}", $"{type} cue",
                $"Keep this {type} and file it{(project is null ? "" : " under " + project)}: \"{Truncate(text, 200)}\"", [segment.SegmentId],
                NoteExtractor.Topic(text), project, Presentation.Ambient, text, type);
        }
        else if (OrganizeCue().IsMatch(text) && project is not null && text.Contains("let's", StringComparison.OrdinalIgnoreCase))
        {
            yield return new JudgeFinding(TaskKind.Organize, 0.5, $"{project}: reorganization mentioned", "organize cue", $"Propose the reorganization implied here for {project}: \"{Truncate(text, 200)}\"", [segment.SegmentId], null, project, Presentation.Proposal);
        }
    }

    private static string? MentionedProject(string text, JudgeContext context)
    {
        foreach (var entry in context.ActiveProjects)
        {
            var name = entry.Split(" (")[0].Trim();
            if (name.Length >= 2 && Regex.IsMatch(text, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)) return name;
        }
        return null;
    }

    private static string? FirstAcronym(string text)
    {
        foreach (Match m in Acronym().Matches(text))
        {
            var term = m.Groups["term"].Value;
            if (!AcronymStopList.Contains(term) && term.Any(char.IsLetter)) return term;
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
