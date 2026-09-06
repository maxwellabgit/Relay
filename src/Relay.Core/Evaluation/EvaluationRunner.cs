using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Judge;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Storage;
using Relay.Core.Tasks;

namespace Relay.Core.Evaluation;

/// <summary>The outcome of one case: what was checked, what failed, and what was observed, so a failure can be read without re-running.</summary>
public sealed record CaseResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("stage")] string Stage,
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
/// The result of evaluating a set: which set (by hash), against which planner and judge, and every
/// case's verdict. A report over an incomplete set has <see cref="Problems"/> and no results; it never
/// passes. Written as JSON so an improve task can cite it as the acceptance evidence for a change set.
/// </summary>
public sealed class EvaluationReport
{
    [JsonPropertyName("setSha256")] public required string SetSha256 { get; init; }
    [JsonPropertyName("ranAt")] public required DateTimeOffset RanAt { get; init; }
    [JsonPropertyName("planner")] public required string Planner { get; init; }
    [JsonPropertyName("judge")] public string? Judge { get; init; }
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
        sb.Append("Evaluation of set ").Append(SetSha256[..12]).Append(" with ").Append(Planner);
        if (Judge is not null) sb.Append(" and judge ").Append(Judge);
        sb.Append(": ").Append(Passed ? "PASS" : "FAIL").Append('\n');
        foreach (var p in Problems) sb.Append("  guard: ").Append(p).Append('\n');
        foreach (var s in Scores) sb.Append("  ").Append(s.Source).Append(": ").Append(s.Passed).Append('/').Append(s.Total).Append('\n');
        foreach (var r in Failed)
        {
            sb.Append("  FAILED ").Append(r.Id).Append(" [").Append(r.Source).Append(", ").Append(r.Stage).Append("] by ").Append(r.Producer).Append('\n');
            foreach (var f in r.Failures) sb.Append("    - ").Append(f).Append('\n');
            sb.Append("    observed: ").Append(r.Observed).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Runs an evaluation set against a planner (and a judge, for cases with segments) and scores each case
/// against its expectation. The planner is called exactly as the coordinator calls it, with a context
/// the caller supplies per case, so the same cases score the deterministic grammar, a scripted model,
/// or a live model. Nothing here executes proposals; only plans and findings are judged.
/// </summary>
public sealed class EvaluationRunner
{
    private readonly IOrchestrator _planner;
    private readonly Func<EvaluationCase, TurnContext> _context;
    private readonly IJudge? _judge;
    private readonly Func<EvaluationCase, JudgeContext>? _judgeContext;
    private readonly Func<DateTimeOffset> _clock;

    public EvaluationRunner(IOrchestrator planner, Func<EvaluationCase, TurnContext> context, IJudge? judge = null, Func<EvaluationCase, JudgeContext>? judgeContext = null, Func<DateTimeOffset>? clock = null)
    {
        _planner = planner;
        _context = context;
        _judge = judge;
        _judgeContext = judgeContext;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Per-case time limit; a planner that does not answer in time fails the case rather than hanging the run.</summary>
    public TimeSpan CaseTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public async Task<EvaluationReport> RunAsync(EvaluationSet set, CancellationToken cancellationToken)
    {
        var problems = set.Validate();
        if (problems.Count > 0)
            return new EvaluationReport { SetSha256 = set.Sha256, RanAt = _clock(), Planner = _planner.Name, Judge = _judge?.Name, Problems = problems };

        var results = new List<CaseResult>(set.Cases.Count);
        foreach (var c in set.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(c.IsJudgeCase ? await RunJudgeCaseAsync(c, cancellationToken).ConfigureAwait(false) : await RunPlanCaseAsync(c, cancellationToken).ConfigureAwait(false));
        }
        return new EvaluationReport { SetSha256 = set.Sha256, RanAt = _clock(), Planner = _planner.Name, Judge = _judge?.Name, Results = results };
    }

    private async Task<CaseResult> RunPlanCaseAsync(EvaluationCase c, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var at = _clock();
        var request = new TurnRequest("eval-" + c.Id, "eval", "eval:" + c.Id, c.Instruction, at, c.ParsedOrigin, c.ParsedKind);
        TurnPlan plan;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CaseTimeout);
            plan = await _planner.PlanAsync(request, _context(c), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CaseResult(c.Id, c.Source, "plan", false, [$"The planner did not answer within {CaseTimeout.TotalSeconds:0}s."], "(timeout)", _planner.Name, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CaseResult(c.Id, c.Source, "plan", false, [$"The planner threw {ex.GetType().Name}: {ex.Message}"], "(exception)", _planner.Name, watch.ElapsedMilliseconds);
        }
        var failures = Score(c.Expect, plan);
        return new CaseResult(c.Id, c.Source, "plan", failures.Count == 0, failures, Observe(plan), plan.Producer, watch.ElapsedMilliseconds);
    }

    private async Task<CaseResult> RunJudgeCaseAsync(EvaluationCase c, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (_judge is null) return new CaseResult(c.Id, c.Source, "judge", false, ["No judge was given to the runner; the case has segments."], "(no judge)", "-", 0);
        var at = _clock();
        var segments = c.Segments!.Select((text, i) => new StreamSegment($"eval-{c.Id}-s{i + 1}", at.AddSeconds(i * 3), text)).ToList();
        var request = new JudgeRequest(TaskOrigin.Observed, segments, segments.Select(s => s.SegmentId).ToList(), null, _judgeContext?.Invoke(c) ?? JudgeContext.Empty, at);
        JudgeDecision decision;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CaseTimeout);
            decision = await _judge.JudgeAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CaseResult(c.Id, c.Source, "judge", false, [$"The judge did not answer within {CaseTimeout.TotalSeconds:0}s."], "(timeout)", _judge.Name, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CaseResult(c.Id, c.Source, "judge", false, [$"The judge threw {ex.GetType().Name}: {ex.Message}"], "(exception)", _judge.Name, watch.ElapsedMilliseconds);
        }
        var failures = Score(c.Expect, decision);
        var observed = decision.Error is not null ? "error: " + decision.Error
            : decision.Findings.Count == 0 ? "no findings"
            : string.Join("; ", decision.Findings.Select(f => $"{f.Kind.Wire()} ({f.Confidence:0.00}): {f.Summary}"));
        return new CaseResult(c.Id, c.Source, "judge", failures.Count == 0, failures, observed, decision.Producer, watch.ElapsedMilliseconds);
    }

    /// <summary>Every way the plan falls short of the expectation, in plain words.</summary>
    public static IReadOnlyList<string> Score(Expectation e, TurnPlan plan)
    {
        var failures = new List<string>();
        var actions = plan.Proposals.Select(p => p.Action).ToList();
        var answer = plan.Answer ?? "";

        if (e.Understood is { } understood && plan.Understood != understood)
            failures.Add(understood ? $"Expected the request to be understood; the planner said: {plan.Summary}" : "Expected the request not to be understood, but a plan was produced.");
        if (e.Actions is { } expected)
        {
            var want = expected.OrderBy(a => a, StringComparer.Ordinal).ToList();
            var got = actions.OrderBy(a => a, StringComparer.Ordinal).ToList();
            if (!want.SequenceEqual(got, StringComparer.Ordinal)) failures.Add($"Expected proposals [{string.Join(", ", want)}] but got [{string.Join(", ", got)}].");
        }
        foreach (var forbidden in e.ForbiddenActions ?? [])
            if (actions.Contains(forbidden, StringComparer.Ordinal)) failures.Add($"'{forbidden}' must not be proposed here.");
        foreach (var fragment in e.AnswerContains ?? [])
            if (!answer.Contains(fragment, StringComparison.OrdinalIgnoreCase)) failures.Add($"The answer does not mention '{fragment}'.");
        foreach (var fragment in e.AnswerAvoids ?? [])
            if (answer.Contains(fragment, StringComparison.OrdinalIgnoreCase)) failures.Add($"The answer must not mention '{fragment}'.");
        if (e.MaxAnswerChars is { } max && answer.Length > max) failures.Add($"The answer is {answer.Length} characters; at most {max} were allowed.");
        var knowledge = plan.Knowledge ?? KnowledgeState.Empty;
        var gap = knowledge.Missing.Count > 0 || knowledge.CapabilityGap;
        if (e.KnowledgeGap is { } wantGap && gap != wantGap) failures.Add(wantGap ? "Expected a stated knowledge gap (missing facts or a capability gap); none was stated." : $"No knowledge gap was expected, but the plan states one: {knowledge.Summary}");
        if (e.CapabilityGap is { } wantCapability && knowledge.CapabilityGap != wantCapability) failures.Add($"Expected capabilityGap={wantCapability.ToString().ToLowerInvariant()} but the plan says {knowledge.CapabilityGap.ToString().ToLowerInvariant()}.");
        if (e.Consistent is { } wantConsistent && plan.Consistent != wantConsistent) failures.Add($"Expected consistent={wantConsistent.ToString().ToLowerInvariant()} but the plan says {(plan.Consistent is null ? "undetermined" : plan.Consistent.Value.ToString().ToLowerInvariant())}.");
        foreach (var assertion in e.Targets ?? [])
        {
            var (action, key, value) = ParseTargetAssertion(assertion);
            if (action is null || key is null) { failures.Add($"Malformed target assertion '{assertion}' (use action.key or action.key=value)."); continue; }
            var candidates = plan.Proposals.Where(p => p.Action == action).ToList();
            if (candidates.Count == 0) { failures.Add($"Target assertion '{assertion}': no '{action}' proposal."); continue; }
            var holds = candidates.Any(p => p.Target.TryGetValue(key, out var v) && v.Length > 0 && (value is null || string.Equals(v, value, StringComparison.OrdinalIgnoreCase)));
            if (!holds) failures.Add($"Target assertion '{assertion}' does not hold; {action} targets: {string.Join(" | ", candidates.Select(p => Render(p.Target)))}.");
        }
        if (e.Contract == true)
        {
            foreach (var p in plan.Proposals.Where(p => p.Action is Actions.UpdatePreference or Actions.UpdatePrompt))
            {
                var missing = PolicyEngine.ContractKeys.Where(k => string.IsNullOrWhiteSpace(p.Target.GetValueOrDefault(k))).ToList();
                if (missing.Count > 0) failures.Add($"{p.Action} lacks the improvement contract field(s): {string.Join(", ", missing)}.");
            }
        }
        return failures;
    }

    /// <summary>Every way the judge's decision falls short of the expectation.</summary>
    public static IReadOnlyList<string> Score(Expectation e, JudgeDecision decision)
    {
        var failures = new List<string>();
        if (decision.Error is not null) failures.Add($"The judge failed: {decision.Error}");
        if (e.Significant is { } significant && decision.Significant != significant)
            failures.Add(significant ? "Expected a significant finding; the judge found nothing." : $"Expected nothing significant, but the judge found: {string.Join("; ", decision.Findings.Select(f => f.Kind.Wire() + ": " + f.Summary))}");
        if (e.FindingKind is { } kind)
        {
            var want = TaskLanes.ParseKind(kind);
            if (!decision.Findings.Any(f => f.Kind == want)) failures.Add($"Expected a '{want.Wire()}' finding; got [{string.Join(", ", decision.Findings.Select(f => f.Kind.Wire()))}].");
        }
        foreach (var forbidden in e.ForbiddenFindingKinds ?? [])
        {
            var kindValue = TaskLanes.ParseKind(forbidden);
            if (decision.Findings.Any(f => f.Kind == kindValue)) failures.Add($"A '{kindValue.Wire()}' finding must not be raised here.");
        }
        return failures;
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

    private static string Observe(TurnPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append(plan.Understood ? "understood" : "not understood").Append("; ");
        sb.Append(plan.Proposals.Count == 0 ? "no proposals" : "proposals: " + string.Join(", ", plan.Proposals.Select(p => p.Action + Render(p.Target, 3))));
        if (plan.Answer is not null) sb.Append("; answer(").Append(plan.Answer.Length).Append("): ").Append(plan.Answer.Length > 160 ? plan.Answer[..159] + "…" : plan.Answer);
        if (plan.Knowledge is { IsEmpty: false } k) sb.Append("; knowledge: missing=").Append(k.Missing.Count).Append(" capabilityGap=").Append(k.CapabilityGap.ToString().ToLowerInvariant());
        if (plan.Consistent is { } consistent) sb.Append("; consistent=").Append(consistent.ToString().ToLowerInvariant());
        return sb.ToString();
    }

    private static string Render(IReadOnlyDictionary<string, string> target, int max = int.MaxValue)
        => "{" + string.Join(", ", target.Take(max).Select(kv => $"{kv.Key}={(kv.Value.Length > 40 ? kv.Value[..39] + "…" : kv.Value)}")) + (target.Count > max ? ", …" : "") + "}";
}
