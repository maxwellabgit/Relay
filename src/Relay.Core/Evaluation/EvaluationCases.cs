using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Policy;
using Relay.Core.Storage;
using Relay.Core.Tasks;

namespace Relay.Core.Evaluation;

/// <summary>
/// Where an evaluation case comes from. A set is complete only when all three are present: recorded
/// cases show what Relay already handled, unseen cases show whether it generalises, failure cases pin
/// the mistakes it must not make again. Recorded cases alone are never the whole evaluation set.
/// </summary>
public static class CaseSources
{
    /// <summary>Derived from a task diagnostics record; the user's response is the label.</summary>
    public const string Recorded = "recorded";
    /// <summary>Authored by hand, never given to Relay before; the held-out set.</summary>
    public const string Unseen = "unseen";
    /// <summary>Authored from a known failure mode; states the mistake it guards against.</summary>
    public const string Failure = "failure";

    public static readonly IReadOnlyList<string> All = [Recorded, Unseen, Failure];
}

/// <summary>
/// What a planner or judge is expected to do with one case. Every property is optional; a property
/// that is null is not checked. An expectation with nothing to check is rejected by the completeness guard.
/// </summary>
public sealed class Expectation
{
    /// <summary>The planner must (or must not) understand the request.</summary>
    [JsonPropertyName("understood")] public bool? Understood { get; init; }
    /// <summary>Exactly these proposal actions, as a multiset (order-free). An empty list means no proposals at all.</summary>
    [JsonPropertyName("actions")] public IReadOnlyList<string>? Actions { get; init; }
    /// <summary>Actions that must not appear among the proposals.</summary>
    [JsonPropertyName("forbiddenActions")] public IReadOnlyList<string>? ForbiddenActions { get; init; }
    /// <summary>Case-insensitive fragments the answer must contain.</summary>
    [JsonPropertyName("answerContains")] public IReadOnlyList<string>? AnswerContains { get; init; }
    /// <summary>Case-insensitive fragments the answer must not contain.</summary>
    [JsonPropertyName("answerAvoids")] public IReadOnlyList<string>? AnswerAvoids { get; init; }
    /// <summary>An upper bound on the answer length (the user's verbosity preference made checkable).</summary>
    [JsonPropertyName("maxAnswerChars")] public int? MaxAnswerChars { get; init; }
    /// <summary>The plan must (or must not) state a knowledge gap: something missing or a capability gap.</summary>
    [JsonPropertyName("knowledgeGap")] public bool? KnowledgeGap { get; init; }
    /// <summary>The plan's capability-gap axis must have this value.</summary>
    [JsonPropertyName("capabilityGap")] public bool? CapabilityGap { get; init; }
    /// <summary>For check tasks: the consistency verdict the plan must reach.</summary>
    [JsonPropertyName("consistent")] public bool? Consistent { get; init; }
    /// <summary>Target assertions of the form <c>action.key=value</c> (exact) or <c>action.key</c> (present, non-empty); each must hold for at least one proposal of that action.</summary>
    [JsonPropertyName("targets")] public IReadOnlyList<string>? Targets { get; init; }
    /// <summary>Every self-change proposal (update_preference, update_prompt) must carry the four improvement-contract fields.</summary>
    [JsonPropertyName("contract")] public bool? Contract { get; init; }
    /// <summary>Judge stage: the judge must (or must not) find something significant in the segments.</summary>
    [JsonPropertyName("significant")] public bool? Significant { get; init; }
    /// <summary>Judge stage: at least one finding must be of this kind.</summary>
    [JsonPropertyName("findingKind")] public string? FindingKind { get; init; }
    /// <summary>Judge stage: no finding may be of any of these kinds.</summary>
    [JsonPropertyName("forbiddenFindingKinds")] public IReadOnlyList<string>? ForbiddenFindingKinds { get; init; }
    /// <summary>Judge stage: a finding (of <see cref="FindingKind"/> when set) must name this project, by name or slug (case-insensitive).</summary>
    [JsonPropertyName("findingProject")] public string? FindingProject { get; init; }
    /// <summary>Judge stage: no finding may name a project (the words concern nothing that is listed; a name must not be invented).</summary>
    [JsonPropertyName("findingNoProject")] public bool? FindingNoProject { get; init; }
    /// <summary>Judge stage: 1-based positions in the window of the segments a finding (of <see cref="FindingKind"/> when set) must cite; grounding.</summary>
    [JsonPropertyName("findingSegments")] public IReadOnlyList<int>? FindingSegments { get; init; }
    /// <summary>Judge stage: at least this many findings (several things happened in the window).</summary>
    [JsonPropertyName("minFindings")] public int? MinFindings { get; init; }
    /// <summary>Judge stage: at most this many findings; selectivity, so one sentence does not become three tasks.</summary>
    [JsonPropertyName("maxFindings")] public int? MaxFindings { get; init; }
    /// <summary>Judge stage: case-insensitive fragments the focused prompt of a finding (of <see cref="FindingKind"/> when set) must contain.</summary>
    [JsonPropertyName("promptContains")] public IReadOnlyList<string>? PromptContains { get; init; }
    /// <summary>Judge stage: case-insensitive fragments the note text of a remember finding must contain (the judge's restatement is what gets filed).</summary>
    [JsonPropertyName("noteContains")] public IReadOnlyList<string>? NoteContains { get; init; }

    // Mind stage (docs/09): the case runs the loop until it ends or first needs the user; the moves are what is scored.
    // A move is written "type" or "type:name" — use_tool:list_projects, propose:create_project, delegate:research, build:world_clock, say, ask_user.
    /// <summary>Mind stage: the very first move must match.</summary>
    [JsonPropertyName("firstMove")] public string? FirstMove { get; init; }
    /// <summary>Mind stage: these moves must occur in this order (not necessarily adjacent).</summary>
    [JsonPropertyName("moves")] public IReadOnlyList<string>? Moves { get; init; }
    /// <summary>Mind stage: none of these moves may occur.</summary>
    [JsonPropertyName("forbiddenMoves")] public IReadOnlyList<string>? ForbiddenMoves { get; init; }
    /// <summary>Mind stage: needs the first read must include (local_notes, world_knowledge, new_tool, external_reasoning, user_input, none).</summary>
    [JsonPropertyName("needs")] public IReadOnlyList<string>? Needs { get; init; }
    /// <summary>Mind stage: the route decision the first read must produce (local, offer_delegate, offer_build, ask_user).</summary>
    [JsonPropertyName("route")] public string? Route { get; init; }
    /// <summary>Mind stage: how the loop must end — answered, or the wait it reached: approval, user, build, delegate.</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    /// <summary>Mind stage: at most this many steps before the loop ends or waits.</summary>
    [JsonPropertyName("maxSteps")] public int? MaxSteps { get; init; }

    /// <summary>True when at least one judge-stage property is set.</summary>
    [JsonIgnore]
    public bool ChecksJudge =>
        Significant is not null || FindingKind is not null || ForbiddenFindingKinds is { Count: > 0 } || FindingProject is not null || FindingNoProject is not null
        || FindingSegments is { Count: > 0 } || MinFindings is not null || MaxFindings is not null || PromptContains is { Count: > 0 } || NoteContains is { Count: > 0 };

    /// <summary>True when at least one mind-stage property is set.</summary>
    [JsonIgnore]
    public bool ChecksMind =>
        FirstMove is not null || Moves is { Count: > 0 } || ForbiddenMoves is { Count: > 0 } || Needs is { Count: > 0 } || Route is not null || Outcome is not null || MaxSteps is not null;

    /// <summary>True when at least one property is set; an expectation that checks nothing is not a case.</summary>
    [JsonIgnore]
    public bool ChecksSomething =>
        Understood is not null || Actions is not null || ForbiddenActions is { Count: > 0 } || AnswerContains is { Count: > 0 } || AnswerAvoids is { Count: > 0 }
        || MaxAnswerChars is not null || KnowledgeGap is not null || CapabilityGap is not null || Consistent is not null || Targets is { Count: > 0 } || Contract is not null
        || ChecksJudge || ChecksMind;
}

/// <summary>
/// One evaluation case: a request Relay is given and what should come of it. A case with
/// <see cref="Segments"/> evaluates the judge (the words are heard while listening); otherwise it
/// evaluates the planner with <see cref="Instruction"/> in the lane named by <see cref="Origin"/> and <see cref="Kind"/>.
/// </summary>
public sealed class EvaluationCase
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "direct";
    /// <summary>The lane kind the planner is asked in (direct asks default to answer); for a judge case, informative only.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    /// <summary>The direct ask verbatim, or the judge's focused prompt for an observed or dialogue task.</summary>
    [JsonPropertyName("instruction")] public string Instruction { get; init; } = "";
    /// <summary>When set, the case evaluates the judge: these are the segments heard, in order.</summary>
    [JsonPropertyName("segments")] public IReadOnlyList<string>? Segments { get; init; }
    /// <summary>
    /// Judge cases: 1-based positions of the segments that are NEW (not yet judged); the others are context the judge
    /// has already seen and must not raise again. Null means every segment is new.
    /// </summary>
    [JsonPropertyName("newSegments")] public IReadOnlyList<int>? NewSegments { get; init; }
    /// <summary>
    /// Observed plan cases: the overheard words the judge kept. The runner hands the planner an excerpt holding exactly these
    /// words (its id is <see cref="EvaluationRunner.ExcerptIdFor"/>), so the planner reads them with read_excerpt as it does
    /// in the application instead of finding them pasted into the instruction.
    /// </summary>
    [JsonPropertyName("heard")] public string? Heard { get; init; }
    /// <summary>
    /// Mind cases: tools the case's world already holds as if Relay had built and promoted them (docs/09 slice 6). The mind
    /// sees them in its tool list; a call returns the scripted <see cref="EvaluationTool.Result"/> instead of running the sandbox.
    /// </summary>
    [JsonPropertyName("tools")] public IReadOnlyList<EvaluationTool>? Tools { get; init; }
    [JsonPropertyName("expect")] public required Expectation Expect { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Why the case exists. Required for failure cases: the mistake it guards against.</summary>
    [JsonPropertyName("why")] public string? Why { get; init; }
    /// <summary>For recorded cases: the task the case was derived from.</summary>
    [JsonPropertyName("recordedTaskId")] public string? RecordedTaskId { get; init; }

    [JsonIgnore] public bool IsJudgeCase => Segments is { Count: > 0 };
    /// <summary>A case scored on the mind's moves (docs/09) rather than on a plan.</summary>
    [JsonIgnore] public bool IsMindCase => !IsJudgeCase && Expect.ChecksMind;

    public TaskOrigin ParsedOrigin => Origin.Trim().ToLowerInvariant() switch
    {
        "observed" => TaskOrigin.Observed,
        "dialogue" => TaskOrigin.Dialogue,
        _ => TaskOrigin.Direct,
    };

    public TaskKind ParsedKind => TaskLanes.ParseKind(Kind);
}

/// <summary>A built tool a mind case's world holds: what the mind is told about it, and the JSON a call returns in evaluation.</summary>
public sealed record EvaluationTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string> Arguments,
    [property: JsonPropertyName("result")] string Result);

/// <summary>
/// A named collection of cases with the completeness guard. <see cref="Validate"/> lists every reason
/// the set is not fit to evaluate against; the runner refuses an incomplete set rather than reporting a
/// flattering score over recorded cases alone.
/// </summary>
public sealed class EvaluationSet
{
    private static readonly string[] KnownOrigins = ["direct", "observed", "dialogue"];
    /// <summary>The wire names and aliases <see cref="TaskLanes.ParseKind"/> accepts.</summary>
    private static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
    {
        "remember", "note", "check", "verify", "resolve", "define", "lookup", "answer", "question", "organize", "organise", "transform", "research", "delegate", "improve", "improvement", "preference",
    };

    public EvaluationSet(IEnumerable<EvaluationCase> cases)
    {
        Cases = cases.ToList();
    }

    public IReadOnlyList<EvaluationCase> Cases { get; }

    public IEnumerable<EvaluationCase> Of(string source) => Cases.Where(c => string.Equals(c.Source, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>The hash of the whole set, so a report names exactly which cases produced its score.</summary>
    public string Sha256
    {
        get
        {
            var json = JsonSerializer.Serialize(Cases.OrderBy(c => c.Id, StringComparer.Ordinal).ToList(), RelayJson.Compact);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }

    /// <summary>Parses a JSON array of cases, or a single case object.</summary>
    public static EvaluationSet Parse(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('['))
            return new EvaluationSet(JsonSerializer.Deserialize<List<EvaluationCase>>(json, RelayJson.Indented) ?? []);
        var single = JsonSerializer.Deserialize<EvaluationCase>(json, RelayJson.Indented);
        return new EvaluationSet(single is null ? [] : [single]);
    }

    /// <summary>Loads every <c>*.json</c> file in a directory (each an array or a single case). A missing directory is an empty set.</summary>
    public static EvaluationSet Load(string directory)
    {
        if (!Directory.Exists(directory)) return new EvaluationSet([]);
        var cases = new List<EvaluationCase>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            cases.AddRange(Parse(File.ReadAllText(file)).Cases);
        return new EvaluationSet(cases);
    }

    public EvaluationSet With(IEnumerable<EvaluationCase> more) => new(Cases.Concat(more));

    public string ToJson() => JsonSerializer.Serialize(Cases, RelayJson.Indented);

    /// <summary>
    /// The completeness guard. Empty when the set may be evaluated. Rules: every case is well formed and
    /// checks something; ids are unique; there is at least one unseen and at least one failure case; each
    /// lane kind that appears in recorded cases also has an unseen case; unseen cases repeat no recorded
    /// instruction; failure cases say what they guard against.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Cases.Count == 0) { problems.Add("The evaluation set is empty."); return problems; }

        foreach (var group in Cases.GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"Case id '{group.Key}' appears {group.Count()} times.");

        foreach (var c in Cases)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) problems.Add("A case has no id.");
            if (!CaseSources.All.Contains(c.Source, StringComparer.OrdinalIgnoreCase)) problems.Add($"Case '{c.Id}': unknown source '{c.Source}' (recorded, unseen, failure).");
            if (!KnownOrigins.Contains(c.Origin.Trim().ToLowerInvariant())) problems.Add($"Case '{c.Id}': unknown origin '{c.Origin}' (direct, observed, dialogue).");
            if (c.Kind is not null && !KnownKinds.Contains(c.Kind.Trim().ToLowerInvariant())) problems.Add($"Case '{c.Id}': unknown kind '{c.Kind}'.");
            if (c.Expect.FindingKind is not null && !KnownKinds.Contains(c.Expect.FindingKind.Trim().ToLowerInvariant())) problems.Add($"Case '{c.Id}': unknown finding kind '{c.Expect.FindingKind}'.");
            if (!c.IsJudgeCase && string.IsNullOrWhiteSpace(c.Instruction)) problems.Add($"Case '{c.Id}': neither an instruction nor segments.");
            if (!c.Expect.ChecksSomething) problems.Add($"Case '{c.Id}': the expectation checks nothing.");
            if (c.IsJudgeCase && (c.Expect.Actions is not null || c.Expect.AnswerContains is not null || c.Expect.Understood is not null))
                problems.Add($"Case '{c.Id}': a judge case (segments) cannot expect planner output (actions, answer, understood).");
            if (!c.IsJudgeCase && c.Expect.ChecksJudge)
                problems.Add($"Case '{c.Id}': a planner case cannot expect judge findings; give it segments.");
            if (c.IsJudgeCase && c.Expect.ChecksMind)
                problems.Add($"Case '{c.Id}': a judge case (segments) cannot expect mind moves.");
            if (c.Expect.ChecksMind && (c.Expect.Actions is not null || c.Expect.Understood is not null || c.Expect.KnowledgeGap is not null || c.Expect.CapabilityGap is not null || c.Expect.Consistent is not null || c.Expect.Targets is { Count: > 0 } || c.Expect.Contract is not null))
                problems.Add($"Case '{c.Id}': a mind case is scored on moves (firstMove, moves, forbiddenMoves, needs, route, outcome, maxSteps, answerContains/avoids); it cannot also expect plan output.");
            foreach (var move in (c.Expect.Moves ?? []).Concat(c.Expect.ForbiddenMoves ?? []).Concat(c.Expect.FirstMove is null ? [] : [c.Expect.FirstMove]))
                if (!Mind.Move.Types.Contains(move.Split(':', 2)[0], StringComparer.Ordinal)) problems.Add($"Case '{c.Id}': '{move}' is not a move (say, use_tool, propose, delegate, build, ask_user, wait, stop).");
            foreach (var need in c.Expect.Needs ?? [])
                if (!Mind.MindRead.KnownNeeds.Contains(need, StringComparer.Ordinal)) problems.Add($"Case '{c.Id}': '{need}' is not a need ({string.Join(", ", Mind.MindRead.KnownNeeds)}).");
            if (!c.IsJudgeCase && c.NewSegments is not null)
                problems.Add($"Case '{c.Id}': newSegments needs segments.");
            if (c.Tools is { Count: > 0 })
            {
                if (!c.IsMindCase) problems.Add($"Case '{c.Id}': tools belong to a mind case (they are the tools the mind may call).");
                foreach (var tool in c.Tools)
                {
                    if (!Tools.ToolPackage.ValidName(tool.Name)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' needs a snake_case name.");
                    else if (Orchestration.ToolBroker.Descriptors.Any(d => d.Name == tool.Name)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' is a built-in tool.");
                    if (string.IsNullOrWhiteSpace(tool.Description)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' needs a description.");
                    if (string.IsNullOrWhiteSpace(tool.Result)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' needs the result a call returns.");
                }
            }
            if (c.Heard is not null && (c.IsJudgeCase || c.ParsedOrigin != TaskOrigin.Observed || string.IsNullOrWhiteSpace(c.Heard)))
                problems.Add($"Case '{c.Id}': heard is the excerpt of an observed plan case; it needs origin=observed, an instruction, and words.");
            if (c.IsJudgeCase)
            {
                var count = c.Segments!.Count;
                foreach (var position in (c.NewSegments ?? []).Concat(c.Expect.FindingSegments ?? []).Where(p => p < 1 || p > count).Distinct())
                    problems.Add($"Case '{c.Id}': segment position {position} is outside the window (1..{count}).");
                if (c.NewSegments is { Count: 0 }) problems.Add($"Case '{c.Id}': newSegments is empty; a judge pass with nothing new is not a case.");
                if (c.Expect.FindingProject is not null && c.Expect.FindingNoProject == true) problems.Add($"Case '{c.Id}': findingProject and findingNoProject contradict each other.");
                if (c.Expect.MinFindings is { } min && c.Expect.MaxFindings is { } max && min > max) problems.Add($"Case '{c.Id}': minFindings exceeds maxFindings.");
                if (c.Expect.Significant == false && (c.Expect.FindingKind is not null || c.Expect.MinFindings > 0)) problems.Add($"Case '{c.Id}': significant=false contradicts an expected finding.");
            }
            if (string.Equals(c.Source, CaseSources.Failure, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(c.Why))
                problems.Add($"Case '{c.Id}': a failure case must say which mistake it guards against (why).");
            foreach (var action in (c.Expect.Actions ?? []).Concat(c.Expect.ForbiddenActions ?? []))
                if (PolicyEngine.TierOf(action) == Tier.Prohibited && !Actions.Prohibited.Contains(action)) problems.Add($"Case '{c.Id}': '{action}' is not an action Relay knows.");
        }

        var recorded = Of(CaseSources.Recorded).ToList();
        var unseen = Of(CaseSources.Unseen).ToList();
        var failure = Of(CaseSources.Failure).ToList();
        if (unseen.Count == 0) problems.Add("No unseen cases: recorded cases alone are never the whole evaluation set.");
        if (failure.Count == 0) problems.Add("No failure cases: the set must pin at least one known mistake.");

        foreach (var kind in recorded.Select(c => c.ParsedKind).Distinct())
            if (!unseen.Any(u => u.ParsedKind == kind)) problems.Add($"Recorded cases cover the '{kind.Wire()}' lane but no unseen case does; add a held-out case for it.");

        var recordedInstructions = recorded.Select(c => Normalize(c.Instruction)).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var u in unseen.Where(u => !u.IsJudgeCase && recordedInstructions.Contains(Normalize(u.Instruction))))
            problems.Add($"Unseen case '{u.Id}' repeats a recorded instruction verbatim; it is not held out.");

        return problems;
    }

    private static string Normalize(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', '!', '?');
}

/// <summary>
/// Turns finished task diagnostics into recorded cases. The user's response is the label. A plan the
/// user accepted whole (every proposal ran, was granted, or was allowed) is expected to come back with
/// the same actions; an action policy denied must not be proposed again; a task that reached an
/// answer or a result was understood; a capability gap the planner stated is expected to be stated
/// again. A rejection is not a label (the user may simply have changed their mind) and is kept only as
/// the case's note. Nothing about an answer's content is assumed; that is what authored cases are for.
/// Recorded cases replay against the current record: one whose world has moved on fails visibly.
/// </summary>
public static class RecordedCases
{
    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal) { "executed", "approved", "allowed", "executing" };

    public static EvaluationCase? From(TaskDiagnostics d)
    {
        if (string.IsNullOrWhiteSpace(d.FocusedPrompt)) return null;
        if (d.Status is not ("completed" or "failed")) return null;
        if (d.Planner is null || d.Outcome is "failed" or "cancelled") return null;             // no plan, or the task never reached the user: nothing to learn from
        if (d.Planner is Producers.User or Producers.Engine or Producers.Judge or Producers.Router) return null;  // not a planner's work
        var acceptedWhole = d.Proposals.Count > 0 && d.Proposals.All(p => Accepted.Contains(p.Status));
        var expected = acceptedWhole ? d.Proposals.Select(p => p.Action).OrderBy(a => a, StringComparer.Ordinal).ToList() : null;
        var denied = d.Proposals.Where(p => p.Status == "denied").Select(p => p.Action).Distinct(StringComparer.Ordinal)
            .Where(a => d.Proposals.All(p => p.Action != a || p.Status == "denied")).OrderBy(a => a, StringComparer.Ordinal).ToList();
        var rejected = d.Proposals.Where(p => p.Status is "rejected" or "edited").Select(p => p.Action).Distinct(StringComparer.Ordinal).ToList();
        var expect = new Expectation
        {
            Understood = true,
            Actions = expected ?? (d.Proposals.Count == 0 ? [] : null),
            ForbiddenActions = denied.Count > 0 ? denied : null,
            CapabilityGap = d.Knowledge.CapabilityGap ? true : null,
        };
        var notes = new List<string>();
        if (d.UserResponse is not null) notes.Add($"user response: {d.UserResponse}");
        if (rejected.Count > 0) notes.Add($"rejected by the user (not a label): {string.Join(", ", rejected)}");
        return new EvaluationCase
        {
            Id = "recorded:" + d.TaskId,
            Source = CaseSources.Recorded,
            Origin = d.Origin,
            Kind = d.Kind,
            Instruction = d.FocusedPrompt,
            Expect = expect,
            Tags = ["recorded", d.Kind, d.Origin, d.Planner],
            Why = notes.Count == 0 ? null : string.Join("; ", notes),
            RecordedTaskId = d.TaskId,
        };
    }

    /// <summary>
    /// Every task record in a data root's tasks folder that yields a case, oldest first. <paramref name="since"/>
    /// keeps only tasks started at or after that moment, so a session's record can be evaluated against the
    /// world it was made in rather than against tasks (creating projects, filing the inbox) that shaped that world.
    /// </summary>
    public static IReadOnlyList<EvaluationCase> Load(string tasksDirectory, DateTimeOffset? since = null)
    {
        if (!Directory.Exists(tasksDirectory)) return [];
        var cases = new List<EvaluationCase>();
        foreach (var file in Directory.EnumerateFiles(tasksDirectory, "*.json").Where(f => !f.EndsWith(".live.json", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal))
        {
            TaskDiagnostics? d;
            try { d = JsonSerializer.Deserialize<TaskDiagnostics>(File.ReadAllText(file), RelayJson.Indented); }
            catch (JsonException) { continue; }
            if (d is null || (since is { } from && d.StartedAt < from)) continue;
            if (From(d) is { } c) cases.Add(c);
        }
        return cases;
    }
}
