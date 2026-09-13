using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Decisions;
using Relay.Core.Mind;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Storage;
using Relay.Core.Tasks;

namespace Relay.Core.Evaluation;

/// <summary>The outcome of one case: what was checked, what failed, and what was observed, so a failure can be read without re-running.</summary>
public sealed record CaseResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("failures")] IReadOnlyList<string> Failures,
    [property: JsonPropertyName("observed")] string Observed,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

/// <summary>Pass counts for one source of cases.</summary>
public sealed record SourceScore(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("passed")] int Passed)
{
    [JsonIgnore] public double Rate => Total == 0 ? 0 : (double)Passed / Total;
}

/// <summary>
/// The result of evaluating a set: which set (by hash), against which mind, and every case's verdict.
/// A report over an incomplete set has <see cref="Problems"/> and no results; it never passes. Written as
/// JSON so an improve task can cite it as the acceptance evidence for a change set.
/// </summary>
public sealed class EvaluationReport
{
    [JsonPropertyName("setSha256")] public required string SetSha256 { get; init; }
    [JsonPropertyName("ranAt")] public required DateTimeOffset RanAt { get; init; }
    [JsonPropertyName("mind")] public required string Mind { get; init; }
    /// <summary>The completeness guard's findings; non-empty means nothing was run.</summary>
    [JsonPropertyName("problems")] public IReadOnlyList<string> Problems { get; init; } = [];
    [JsonPropertyName("results")] public IReadOnlyList<CaseResult> Results { get; init; } = [];

    [JsonIgnore] public bool Passed => Problems.Count == 0 && Results.Count > 0 && Results.All(r => r.Passed);

    [JsonPropertyName("scores")]
    public IReadOnlyList<SourceScore> Scores => CaseSources.All
        .Select(s => new SourceScore(s, Results.Count(r => r.Source == s), Results.Count(r => r.Source == s && r.Passed)))
        .Where(s => s.Total > 0)
        .ToList();

    public IEnumerable<CaseResult> Failed => Results.Where(r => !r.Passed);

    public string ToJson() => JsonSerializer.Serialize(this, RelayJson.Indented);

    /// <summary>A plain-text rendering: one line per source, then every failed case with its reasons and what was observed.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("Evaluation of set ").Append(SetSha256[..12]).Append(" with ").Append(Mind);
        sb.Append(": ").Append(Passed ? "PASS" : "FAIL").Append('\n');
        foreach (var p in Problems) sb.Append("  guard: ").Append(p).Append('\n');
        foreach (var s in Scores) sb.Append("  ").Append(s.Source).Append(": ").Append(s.Passed).Append('/').Append(s.Total).Append('\n');
        foreach (var r in Failed)
        {
            sb.Append("  FAILED ").Append(r.Id).Append(" [").Append(r.Source).Append("] by ").Append(r.Producer).Append('\n');
            foreach (var f in r.Failures) sb.Append("    - ").Append(f).Append('\n');
            sb.Append("    observed: ").Append(r.Observed).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Runs an evaluation set against a mind and scores each case against its expectation. The loop is built
/// exactly as the coordinator builds it, over a world the caller supplies per case, so the same cases
/// score a scripted mind or a live model. Nothing here is executed: read-only tools run for real, and
/// anything that would need the user leaves the loop waiting where the engine would have asked.
/// </summary>
public sealed class EvaluationRunner
{
    private readonly IMind _mind;
    private readonly Func<EvaluationCase, TurnContext> _world;
    private readonly Func<EvaluationCase, MindContext> _context;
    private readonly Func<DateTimeOffset> _clock;

    public EvaluationRunner(IMind mind, Func<EvaluationCase, TurnContext> world, Func<EvaluationCase, MindContext> context, Func<DateTimeOffset>? clock = null)
    {
        _mind = mind;
        _world = world;
        _context = context;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Per-case time limit; a mind that does not answer in time fails the case rather than hanging the run.</summary>
    public TimeSpan CaseTimeout { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>The loop's step budget when the case sets none.</summary>
    public int MaxSteps { get; init; } = 8;

    public async Task<EvaluationReport> RunAsync(EvaluationSet set, CancellationToken cancellationToken)
    {
        var problems = set.Validate();
        if (problems.Count > 0)
            return new EvaluationReport { SetSha256 = set.Sha256, RanAt = _clock(), Mind = _mind.Name, Problems = problems };

        var results = new List<CaseResult>(set.Cases.Count);
        foreach (var c in set.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunCaseAsync(c, cancellationToken).ConfigureAwait(false));
        }
        return new EvaluationReport { SetSha256 = set.Sha256, RanAt = _clock(), Mind = _mind.Name, Results = results };
    }

    // ----------------------------------------------------------------------------------------
    // One case: the loop runs against the case's world until it ends or first needs the user
    // ----------------------------------------------------------------------------------------

    private async Task<CaseResult> RunCaseAsync(EvaluationCase c, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var at = _clock();
        var turn = _world(c);
        var context = _context(c);
        var built = c.Tools ?? [];
        if (built.Count > 0) context.Tools = [.. context.Tools.Where(t => built.All(b => b.Name != t.Name)), .. built.Select(b => new ToolDescriptor(b.Name, b.Description, b.Arguments))];
        var host = new EvaluationHost(turn.Tools, built, _clock);
        var source = c.ParsedOrigin switch { TaskOrigin.Observed => InputObserved.Heard, TaskOrigin.Dialogue => InputObserved.FollowUp, _ => InputObserved.Ask };
        var loop = new TaskLoop("eval-" + c.Id, source, _mind, host, context, new Decider(DecisionSet.Default()), new LoopBudget(c.Expect.MaxSteps ?? MaxSteps, turn.Settings.MaxToolCalls), new FixedClock(_clock));
        loop.Observe(new InputObserved(at, source, c.Instruction, c.Heard is null ? null : ExcerptIdFor(c)));
        LoopResult? result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaseTimeout);
        try
        {
            result = await loop.RunAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CaseResult(c.Id, c.Source, false, [$"The mind threw {ex.GetType().Name}: {ex.Message}"], Observe(loop, null), _mind.Name, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = null;
        }
        // The loop absorbs a cancellation into a failed result, so a mind that never answers would otherwise be
        // reported as a cancelled task. The case's own clock ran out; that is the one thing worth saying about it.
        if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            return new CaseResult(c.Id, c.Source, false, [$"The mind did not finish within {CaseTimeout.TotalSeconds:0.##}s."], Observe(loop, null), _mind.Name, watch.ElapsedMilliseconds);
        var failures = Score(c.Expect, loop, result);
        return new CaseResult(c.Id, c.Source, failures.Count == 0, failures, Observe(loop, result), _mind.Name, watch.ElapsedMilliseconds);
    }

    /// <summary>The outcome of a mind case: "answered" (or another end), or the wait it stopped at (approval, user, build, delegate).</summary>
    public static string OutcomeOf(TaskLoop loop, LoopResult? result) => result is not null ? result.Outcome : loop.WaitingFor ?? "waiting";

    /// <summary>Every way the mind's moves fall short of the expectation.</summary>
    public static IReadOnlyList<string> Score(Expectation e, TaskLoop loop, LoopResult? result)
    {
        var failures = new List<string>();
        var moves = loop.Transcript.OfType<MoveObserved>().Select(m => m.Move).ToList();
        var written = moves.Select(Written).ToList();
        // A loop that failed fails its case, unless failing is what the case pins.
        if (result is { Status: LoopStatus.Failed } && e.Completes != false) failures.Add($"The loop failed: {result.Error ?? result.Outcome}.");
        if (e.Completes == false && result is not { Status: LoopStatus.Failed }) failures.Add("Expected the loop to fail; it ran to an end.");
        if (e.FirstMove is { } first && (moves.Count == 0 || !Matches(moves[0], first)))
            failures.Add($"Expected the first move to be {first}; it was {(moves.Count == 0 ? "nothing" : written[0])}.");
        if (e.Moves is { Count: > 0 } wanted)
        {
            var i = 0;
            foreach (var move in moves) { if (i < wanted.Count && Matches(move, wanted[i])) i++; }
            if (i < wanted.Count) failures.Add($"Expected the moves [{string.Join(", ", wanted)}] in order; missing {wanted[i]} in [{string.Join(", ", written)}].");
        }
        foreach (var forbidden in e.ForbiddenMoves ?? [])
            if (moves.Any(m => Matches(m, forbidden))) failures.Add($"The move {forbidden} must not be made here.");
        foreach (var need in e.Needs ?? [])
            if (loop.FirstRead is null || !loop.FirstRead.Has(need)) failures.Add($"Expected the first read to need '{need}'; it needed [{string.Join(", ", loop.FirstRead?.Needs ?? [])}].");
        if (e.Route is { } route && !string.Equals(loop.Route?.Outcome, route, StringComparison.Ordinal)) failures.Add($"Expected the route '{route}'; the decider chose '{loop.Route?.Outcome ?? "none"}'.");
        var outcome = OutcomeOf(loop, result);
        if (e.Outcome is { } wantOutcome && !string.Equals(outcome, wantOutcome, StringComparison.Ordinal)) failures.Add($"Expected the outcome '{wantOutcome}'; it was '{outcome}'.");
        if (e.MaxSteps is { } max && loop.Steps > max) failures.Add($"Took {loop.Steps} steps; at most {max} were allowed.");
        var answer = result?.Answer ?? "";
        foreach (var fragment in e.AnswerContains ?? [])
            if (!answer.Contains(fragment, StringComparison.OrdinalIgnoreCase)) failures.Add($"The answer does not mention '{fragment}'.");
        foreach (var fragment in e.AnswerAvoids ?? [])
            if (answer.Contains(fragment, StringComparison.OrdinalIgnoreCase)) failures.Add($"The answer must not mention '{fragment}'.");
        if (e.MaxAnswerChars is { } maxChars && answer.Length > maxChars) failures.Add($"The answer is {answer.Length} characters; at most {maxChars} were allowed.");
        if (e.Consistent is { } wantVerdict && loop.Consistent != wantVerdict)
            failures.Add($"Expected the verdict consistent={Word(wantVerdict)} but the mind said {(loop.Consistent is null ? "nothing" : Word(loop.Consistent.Value))}.");

        // A move says what was proposed; these say what it was proposed about, which is where a plausible-looking proposal goes wrong.
        var proposals = moves.OfType<ProposeMove>().ToList();
        foreach (var assertion in e.Targets ?? [])
        {
            var (action, key, value) = ParseTargetAssertion(assertion);
            if (action is null || key is null) { failures.Add($"Malformed target assertion '{assertion}' (use action.key or action.key=value)."); continue; }
            var candidates = proposals.Where(p => string.Equals(p.Action, action, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) { failures.Add($"Target assertion '{assertion}': no '{action}' proposal."); continue; }
            var holds = candidates.Any(p => p.Target.TryGetValue(key, out var v) && v.Length > 0 && (value is null || string.Equals(v, value, StringComparison.OrdinalIgnoreCase)));
            if (!holds) failures.Add($"Target assertion '{assertion}' does not hold; {action} targets: {string.Join(" | ", candidates.Select(p => Render(p.Target)))}.");
        }
        if (e.Contract == true)
            foreach (var p in proposals.Where(p => p.Action is Actions.UpdatePreference or Actions.UpdatePrompt))
            {
                var missing = PolicyEngine.ContractKeys.Where(k => string.IsNullOrWhiteSpace(p.Target.GetValueOrDefault(k))).ToList();
                if (missing.Count > 0) failures.Add($"{p.Action} lacks the improvement contract field(s): {string.Join(", ", missing)}.");
            }
        return failures;

        static string Word(bool consistent) => consistent ? MindRead.Agrees : MindRead.Conflicts;
    }

    private static (string? Action, string? Key, string? Value) ParseTargetAssertion(string assertion)
    {
        var eq = assertion.IndexOf('=');
        var path = eq < 0 ? assertion : assertion[..eq];
        var value = eq < 0 ? null : assertion[(eq + 1)..];
        var dot = path.IndexOf('.');
        if (dot <= 0 || dot == path.Length - 1) return (null, null, null);
        // "model.request.profile" has a dot inside the action name: the key is the last segment.
        var lastDot = path.LastIndexOf('.');
        return (path[..lastDot], path[(lastDot + 1)..], value);
    }

    private static string Render(IReadOnlyDictionary<string, string> target)
        => "{" + string.Join(", ", target.Select(kv => $"{kv.Key}={(kv.Value.Length > 40 ? kv.Value[..39] + "…" : kv.Value)}")) + "}";

    /// <summary>"type" or "type:name" — the name is the tool, action, profile or tool-to-build; a name of "*" matches any.</summary>
    private static bool Matches(Move move, string pattern)
    {
        var parts = pattern.Split(':', 2);
        if (!string.Equals(move.Type, parts[0], StringComparison.Ordinal)) return false;
        if (parts.Length == 1 || parts[1] == "*") return true;
        var name = move switch { UseToolMove t => t.Tool, ProposeMove p => p.Action, DelegateMove d => d.Profile, BuildMove b => b.Name, RunWorkflowMove w => w.Name, _ => null };
        return string.Equals(name, parts[1], StringComparison.OrdinalIgnoreCase);
    }

    private static string Written(Move move) => move switch
    {
        UseToolMove t => $"use_tool:{t.Tool}",
        ProposeMove p => $"propose:{p.Action}",
        DelegateMove d => $"delegate:{d.Profile}",
        BuildMove b => $"build:{b.Name}",
        RunWorkflowMove w => $"run_workflow:{w.Name}",
        _ => move.Type,
    };

    private static string Observe(TaskLoop loop, LoopResult? result)
    {
        var sb = new StringBuilder();
        sb.Append("moves: ").Append(string.Join(" → ", loop.Transcript.OfType<MoveObserved>().Select(m => Written(m.Move))));
        sb.Append("; outcome: ").Append(OutcomeOf(loop, result));
        if (loop.FirstRead is { } r) sb.Append("; read: complexity=").Append(r.Complexity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(" needs=[").Append(string.Join(",", r.Needs)).Append("] intent=").Append(r.Intent.Length > 100 ? r.Intent[..99] + "…" : r.Intent);
        if (loop.Route is { } route) sb.Append("; route=").Append(route.Outcome);
        if (result?.Answer is { } answer) sb.Append("; answer(").Append(answer.Length).Append("): ").Append(answer.Length > 160 ? answer[..159] + "…" : answer);
        if (loop.Feed.Count > 0) sb.Append("; feed: ").Append(string.Join(" | ", loop.Feed.Take(8).Select(s => s.Length > 120 ? s[..119] + "…" : s)));
        if (loop.PromptTokens > 0 || loop.CompletionTokens > 0) sb.Append(" · ").Append(loop.PromptTokens).Append('+').Append(loop.CompletionTokens).Append(" tok");
        // The trace: each move with its arguments and what it caused, so a live run can be read without re-running it.
        var trace = loop.Transcript.Skip(1).Where(o => o is not MoveObserved { Move: SayMove }).Select(o => o switch
        {
            MoveObserved m => m.Move.Brief(),
            ToolObserved t => $"→ {(t.Ok ? "ok" : "error")} · {Observation.Clip(t.Summary, 120)}",
            SystemObserved s => $"→ system · {Observation.Clip(s.Text, 160)}",
            PolicyObserved p => $"→ policy {p.Outcome}",
            BuildObserved b => $"→ build {b.Stage}",
            DelegateObserved d => $"→ delegate {d.Stage}",
            _ => $"→ {o.Kind}",
        }).ToList();
        if (trace.Count > 0) sb.Append("; trace: ").Append(string.Join(" ", trace));
        return sb.ToString();
    }

    /// <summary>
    /// The loop's host in evaluation: read-only tools run for real against the case's world; anything that would need the user
    /// (a proposal, a delegation, a build, a question) is answered with the observation the engine would give and the loop is left
    /// waiting there — that first wait is the case's outcome. Nothing is executed.
    /// </summary>
    private sealed class EvaluationHost : ILoopHost
    {
        private readonly ToolBroker _tools;
        private readonly IReadOnlyList<EvaluationTool> _built;
        private readonly Func<DateTimeOffset> _clock;

        public EvaluationHost(ToolBroker tools, IReadOnlyList<EvaluationTool> built, Func<DateTimeOffset> clock) { _tools = tools; _built = built; _clock = clock; }

        public void Stepped(TaskLoop loop, MindStep step) { }
        public void Said(TaskLoop loop, SayMove move) { }
        public void Waiting(TaskLoop loop, string waitingFor) { }
        public void Ended(TaskLoop loop, LoopResult result) { }

        public Task<MoveOutcome> UseToolAsync(TaskLoop loop, UseToolMove move, CancellationToken cancellationToken)
        {
            // A tool the case's world holds as built: the scripted result stands in for the sandbox run.
            if (_built.FirstOrDefault(b => b.Name == move.Tool) is { } scripted)
                return Task.FromResult(MoveOutcome.Of(new ToolObserved(_clock(), move.Tool, move.Args, true, "ok · " + Observation.Clip(scripted.Result, 300), scripted.Result, [])));
            var result = _tools.Call(move.Tool, move.Args);
            string? data = null;
            if (result.Ok && result.Data is not null) { try { data = JsonSerializer.Serialize(result.Data, RelayJson.Compact); } catch (NotSupportedException) { } }
            var ids = result.Hits?.Select(h => h.Id).Distinct(StringComparer.Ordinal).ToList() ?? [];
            return Task.FromResult(MoveOutcome.Of(new ToolObserved(_clock(), move.Tool, move.Args, result.Ok, result.Ok ? result.Summary : result.Error ?? "failed", data, ids)));
        }

        public Task<MoveOutcome> ProposeAsync(TaskLoop loop, ProposeMove move, DecisionRecord? fof, CancellationToken cancellationToken)
        {
            var tier = PolicyEngine.TierOf(move.Action);
            var now = _clock();
            if (tier == Tier.Prohibited) return Task.FromResult(MoveOutcome.Of(new PolicyObserved(now, "eval", move.Action, PolicyObserved.Denied, [$"'{move.Action}' is prohibited."])));
            return Task.FromResult(MoveOutcome.Wait(Waits.Approval, new PolicyObserved(now, "eval", move.Action, PolicyObserved.NeedsApproval, ["Evaluation: proposals are scored, not executed."])));
        }

        public Task<MoveOutcome> DelegateAsync(TaskLoop loop, DelegateMove move, CancellationToken cancellationToken)
            => Task.FromResult(MoveOutcome.Wait(Waits.Approval, new PolicyObserved(_clock(), "eval", Actions.ModelRequest, PolicyObserved.NeedsApproval, ["Evaluation: the package is scored, not sent."])));

        public Task<MoveOutcome> BuildAsync(TaskLoop loop, BuildMove move, DecisionRecord fof, CancellationToken cancellationToken)
            => Task.FromResult(MoveOutcome.Wait(Waits.Build, new BuildObserved(_clock(), move.Name, BuildObserved.Started, "Evaluation: the build request is scored, not built.")));

        public Task<MoveOutcome> RunWorkflowAsync(TaskLoop loop, RunWorkflowMove move, CancellationToken cancellationToken)
            => Task.FromResult(MoveOutcome.Of(new WorkflowObserved(_clock(), move.Name, WorkflowObserved.Finished, "Evaluation: workflows are scored as a single move.")));

        public Task<MoveOutcome> AskUserAsync(TaskLoop loop, AskUserMove move, CancellationToken cancellationToken)
            => Task.FromResult(MoveOutcome.Wait(Waits.User));

        public Task<MoveOutcome> StopAsync(TaskLoop loop, StopMove move, string waitingFor, CancellationToken cancellationToken)
            => Task.FromResult(MoveOutcome.Of(new SystemObserved(_clock(), "Nothing is running in evaluation.")));
    }

    private sealed class FixedClock : Time.IClock
    {
        private readonly Func<DateTimeOffset> _now;
        public FixedClock(Func<DateTimeOffset> now) => _now = now;
        public DateTimeOffset UtcNow => _now();
    }

    /// <summary>
    /// The excerpt id an observed case's <see cref="EvaluationCase.Heard"/> words are stored under. The world factory
    /// owns the excerpt store, so it persists the words under this id before the loop runs; the runner puts the same id
    /// on the input the mind observes. Safe as a file name: everything outside letters, digits, '-' and '_' becomes '-'.
    /// </summary>
    public static string ExcerptIdFor(EvaluationCase c) =>
        "eval-heard-" + new string(c.Id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
}
