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
/// What the mind is expected to do with one case. Every property is optional; a property that is null is
/// not checked. An expectation with nothing to check is rejected by the completeness guard.
///
/// A case is scored on the moves the loop made, which is the only thing the mind produces: a move is
/// written "type" or "type:name" — use_tool:list_projects, propose:create_project, delegate:research,
/// build:world_clock, say, ask_user — and a name of "*" matches any.
/// </summary>
public sealed class Expectation
{
    /// <summary>The very first move must match.</summary>
    [JsonPropertyName("firstMove")] public string? FirstMove { get; init; }
    /// <summary>These moves must occur in this order (not necessarily adjacent).</summary>
    [JsonPropertyName("moves")] public IReadOnlyList<string>? Moves { get; init; }
    /// <summary>None of these moves may occur. "propose" with no action forbids proposing anything at all.</summary>
    [JsonPropertyName("forbiddenMoves")] public IReadOnlyList<string>? ForbiddenMoves { get; init; }
    /// <summary>Needs the first read must include (local_notes, world_knowledge, new_tool, external_reasoning, user_input, none).</summary>
    [JsonPropertyName("needs")] public IReadOnlyList<string>? Needs { get; init; }
    /// <summary>The route decision the first read must produce (local, offer_delegate, offer_build, ask_user).</summary>
    [JsonPropertyName("route")] public string? Route { get; init; }
    /// <summary>How the loop must end — answered, or the wait it reached: approval, user, build, delegate.</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    /// <summary>At most this many steps before the loop ends or waits.</summary>
    [JsonPropertyName("maxSteps")] public int? MaxSteps { get; init; }
    /// <summary>The loop must reach an end or a wait rather than failing. False pins a case where failing is the right outcome.</summary>
    [JsonPropertyName("completes")] public bool? Completes { get; init; }
    /// <summary>Case-insensitive fragments the answer must contain.</summary>
    [JsonPropertyName("answerContains")] public IReadOnlyList<string>? AnswerContains { get; init; }
    /// <summary>Case-insensitive fragments the answer must not contain.</summary>
    [JsonPropertyName("answerAvoids")] public IReadOnlyList<string>? AnswerAvoids { get; init; }
    /// <summary>An upper bound on the answer length (the user's verbosity preference made checkable).</summary>
    [JsonPropertyName("maxAnswerChars")] public int? MaxAnswerChars { get; init; }
    /// <summary>For check tasks: the consistency verdict that must be reached.</summary>
    [JsonPropertyName("consistent")] public bool? Consistent { get; init; }
    /// <summary>
    /// What a proposal must be about, as <c>action.key=value</c> (exact) or <c>action.key</c> (present, non-empty);
    /// each must hold for at least one propose move of that action. A move says what was proposed; this says what
    /// it was proposed about, which is where a plausible-looking proposal goes wrong.
    /// </summary>
    [JsonPropertyName("targets")] public IReadOnlyList<string>? Targets { get; init; }
    /// <summary>Every self-change proposal (update_preference, update_prompt) must carry the four improvement-contract fields.</summary>
    [JsonPropertyName("contract")] public bool? Contract { get; init; }

    /// <summary>True when at least one property is set; an expectation that checks nothing is not a case.</summary>
    [JsonIgnore]
    public bool ChecksSomething =>
        FirstMove is not null || Moves is { Count: > 0 } || ForbiddenMoves is { Count: > 0 } || Needs is { Count: > 0 } || Route is not null
        || Outcome is not null || MaxSteps is not null || Completes is not null || AnswerContains is { Count: > 0 } || AnswerAvoids is { Count: > 0 }
        || MaxAnswerChars is not null || Consistent is not null || Targets is { Count: > 0 } || Contract is not null;
}

/// <summary>
/// One evaluation case: a request Relay is given and what should come of it. The mind's loop runs it
/// with <see cref="Instruction"/> in the lane named by <see cref="Origin"/> and <see cref="Kind"/>,
/// and the moves it makes are what is scored.
/// </summary>
public sealed class EvaluationCase
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "direct";
    /// <summary>The lane kind the planner is asked in (direct asks default to answer); for a mind case, informative only.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    /// <summary>The direct ask verbatim, or the objective of an observed or dialogue task.</summary>
    [JsonPropertyName("instruction")] public string Instruction { get; init; } = "";
    /// <summary>
    /// Observed cases: the overheard words that were kept. The runner stores an excerpt holding exactly these words (its id
    /// is <see cref="EvaluationRunner.ExcerptIdFor"/>) and puts its id on the task, so the words are read with read_excerpt
    /// as they are in the application instead of being found pasted into the instruction.
    /// </summary>
    [JsonPropertyName("heard")] public string? Heard { get; init; }
    /// <summary>
    /// Tools the case's world already holds as if Relay had built and promoted them (docs/09 slice 6). The mind
    /// sees them in its tool list; a call returns the scripted <see cref="EvaluationTool.Result"/> instead of running the sandbox.
    /// </summary>
    [JsonPropertyName("tools")] public IReadOnlyList<EvaluationTool>? Tools { get; init; }
    [JsonPropertyName("expect")] public required Expectation Expect { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Why the case exists. Required for failure cases: the mistake it guards against.</summary>
    [JsonPropertyName("why")] public string? Why { get; init; }
    /// <summary>For recorded cases: the task the case was derived from.</summary>
    [JsonPropertyName("recordedTaskId")] public string? RecordedTaskId { get; init; }

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
            if (string.IsNullOrWhiteSpace(c.Instruction)) problems.Add($"Case '{c.Id}': no instruction.");
            if (!c.Expect.ChecksSomething) problems.Add($"Case '{c.Id}': the expectation checks nothing.");
            foreach (var move in (c.Expect.Moves ?? []).Concat(c.Expect.ForbiddenMoves ?? []).Concat(c.Expect.FirstMove is null ? [] : [c.Expect.FirstMove]))
            {
                var parts = move.Split(':', 2);
                if (!Mind.Move.Types.Contains(parts[0], StringComparer.Ordinal)) { problems.Add($"Case '{c.Id}': '{move}' is not a move ({string.Join(", ", Mind.Move.Types)})."); continue; }
                // A misspelled action would make a propose move that can never match, or a forbidden one that forbids nothing.
                if (parts[0] == Mind.Move.Propose && parts.Length == 2 && parts[1] != "*" && PolicyEngine.TierOf(parts[1]) == Tier.Prohibited && !Actions.Prohibited.Contains(parts[1]))
                    problems.Add($"Case '{c.Id}': '{parts[1]}' is not an action Relay knows.");
            }
            foreach (var need in c.Expect.Needs ?? [])
                if (!Mind.MindRead.KnownNeeds.Contains(need, StringComparer.Ordinal)) problems.Add($"Case '{c.Id}': '{need}' is not a need ({string.Join(", ", Mind.MindRead.KnownNeeds)}).");
            if (c.Tools is { Count: > 0 })
            {
                foreach (var tool in c.Tools)
                {
                    if (!Tools.ToolPackage.ValidName(tool.Name)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' needs a snake_case name.");
                    else if (Orchestration.ToolBroker.Descriptors.Any(d => d.Name == tool.Name)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' is a built-in tool.");
                    if (string.IsNullOrWhiteSpace(tool.Description)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' needs a description.");
                    if (string.IsNullOrWhiteSpace(tool.Result)) problems.Add($"Case '{c.Id}': tool '{tool.Name}' needs the result a call returns.");
                }
            }
            if (c.Heard is not null && (c.ParsedOrigin != TaskOrigin.Observed || string.IsNullOrWhiteSpace(c.Heard)))
                problems.Add($"Case '{c.Id}': heard is the excerpt of an observed case; it needs origin=observed, an instruction, and words.");
            if (string.Equals(c.Source, CaseSources.Failure, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(c.Why))
                problems.Add($"Case '{c.Id}': a failure case must say which mistake it guards against (why).");
        }

        var recorded = Of(CaseSources.Recorded).ToList();
        var unseen = Of(CaseSources.Unseen).ToList();
        var failure = Of(CaseSources.Failure).ToList();
        if (unseen.Count == 0) problems.Add("No unseen cases: recorded cases alone are never the whole evaluation set.");
        if (failure.Count == 0) problems.Add("No failure cases: the set must pin at least one known mistake.");

        foreach (var kind in recorded.Select(c => c.ParsedKind).Distinct())
            if (!unseen.Any(u => u.ParsedKind == kind)) problems.Add($"Recorded cases cover the '{kind.Wire()}' lane but no unseen case does; add a held-out case for it.");

        var recordedInstructions = recorded.Select(c => Normalize(c.Instruction)).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var u in unseen.Where(u => recordedInstructions.Contains(Normalize(u.Instruction))))
            problems.Add($"Unseen case '{u.Id}' repeats a recorded instruction verbatim; it is not held out.");

        return problems;
    }

    private static string Normalize(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', '!', '?');
}

/// <summary>
/// Turns finished task diagnostics into recorded cases. The user's response is the label. A task the
/// user accepted whole (every proposal ran, was granted, or was allowed) is expected to propose the
/// same things again, in the same order; an action policy denied must not be proposed again; a task
/// that proposed nothing must propose nothing again; a capability gap the mind stated is expected to
/// be stated again. A rejection is not a label (the user may simply have changed their mind) and is
/// kept only as the case's note, so such a case asks no more than that the loop gets somewhere.
/// Nothing about an answer's content is assumed; that is what authored cases are for. Recorded cases
/// replay against the current record: one whose world has moved on fails visibly.
/// </summary>
public static class RecordedCases
{
    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal) { "executed", "approved", "allowed", "executing" };

    public static EvaluationCase? From(TaskDiagnostics d)
    {
        if (string.IsNullOrWhiteSpace(d.FocusedPrompt)) return null;
        if (d.Status is not ("completed" or "failed")) return null;
        if (d.Planner is null || d.Outcome is "failed" or "cancelled") return null;             // nothing ran, or the task never reached the user: nothing to learn from
        if (d.Planner is Producers.User or Producers.Engine or Producers.Router) return null;  // not the mind's work
        var acceptedWhole = d.Proposals.Count > 0 && d.Proposals.All(p => Accepted.Contains(p.Status));
        var denied = d.Proposals.Where(p => p.Status == "denied").Select(p => p.Action).Distinct(StringComparer.Ordinal)
            .Where(a => d.Proposals.All(p => p.Action != a || p.Status == "denied")).OrderBy(a => a, StringComparer.Ordinal).ToList();
        var rejected = d.Proposals.Where(p => p.Status is "rejected" or "edited").Select(p => p.Action).Distinct(StringComparer.Ordinal).ToList();
        var expect = new Expectation
        {
            Completes = true,
            Moves = acceptedWhole ? d.Proposals.Select(p => Mind.Move.Propose + ":" + p.Action).ToList() : null,
            ForbiddenMoves = denied.Count > 0 ? denied.Select(a => Mind.Move.Propose + ":" + a).ToList()
                : d.Proposals.Count == 0 ? [Mind.Move.Propose] : null,
            Needs = d.Knowledge.CapabilityGap ? [Mind.MindRead.NeedNewTool] : null,
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
