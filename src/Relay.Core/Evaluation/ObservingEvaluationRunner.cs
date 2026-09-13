using System.Diagnostics;
using System.Text;
using Relay.Core.Decisions;
using Relay.Core.Mind;
using Relay.Core.Time;

namespace Relay.Core.Evaluation;

/// <summary>
/// The Alpha gate's observing stage: one pass of <see cref="ObservingLoop"/> against a recorded host,
/// scored on what the mind raised (and refused). Nothing is executed; raises are recorded and returned
/// as observations the way the coordinator would hand work to a task.
/// </summary>
public sealed class ObservingEvaluationRunner
{
    private readonly IMind _mind;
    private readonly Func<EvaluationCase, MindContext> _context;
    private readonly Func<DateTimeOffset> _clock;

    public ObservingEvaluationRunner(IMind mind, Func<EvaluationCase, MindContext> context, Func<DateTimeOffset>? clock = null)
    {
        _mind = mind;
        _context = context;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan CaseTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<EvaluationReport> RunAsync(EvaluationSet set, CancellationToken cancellationToken)
    {
        var problems = set.Validate();
        if (problems.Count > 0)
            return new EvaluationReport { SetSha256 = set.Sha256, RanAt = _clock(), Mind = _mind.Name, Problems = problems };

        var missing = set.Cases.Where(c => !c.IsObserving).Select(c => c.Id).ToList();
        if (missing.Count > 0)
            return new EvaluationReport
            {
                SetSha256 = set.Sha256,
                RanAt = _clock(),
                Mind = _mind.Name,
                Problems = [$"Observing stage needs a window on every case; missing on: {string.Join(", ", missing)}."],
            };

        var results = new List<CaseResult>(set.Cases.Count);
        foreach (var c in set.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunCaseAsync(c, cancellationToken).ConfigureAwait(false));
        }
        return new EvaluationReport { SetSha256 = set.Sha256, RanAt = _clock(), Mind = _mind.Name, Results = results };
    }

    private async Task<CaseResult> RunCaseAsync(EvaluationCase c, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var at = _clock();
        var host = new RecordingHost(at);
        var context = _context(c);
        var loop = new ObservingLoop("eval-obs-" + c.Id, _mind, host, context, new Decider(DecisionSet.Default()), clock: new FixedClock(_clock));
        var window = c.Window ?? [];
        var observed = new WindowObserved(at, loop.StreamId,
            window.Select((l, i) => new WindowLine(l.Label, "seg-" + (i + 1), l.Text)).ToList(),
            [],
            window.Sum(l => l.Text.Length));

        PassResult? pass;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaseTimeout);
        try
        {
            pass = await loop.ObserveAsync(observed, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CaseResult(c.Id, c.Source, false, [$"The mind threw {ex.GetType().Name}: {ex.Message}"], Observe(loop, host, null), _mind.Name, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CaseResult(c.Id, c.Source, false, [$"The mind did not finish within {CaseTimeout.TotalSeconds:0.##}s."], Observe(loop, host, null), _mind.Name, watch.ElapsedMilliseconds);
        }

        var failures = Score(c.Expect, loop, host, pass);
        return new CaseResult(c.Id, c.Source, failures.Count == 0, failures, Observe(loop, host, pass), _mind.Name, watch.ElapsedMilliseconds);
    }

    /// <summary>Every way the observing pass falls short of the expectation.</summary>
    public static IReadOnlyList<string> Score(Expectation e, ObservingLoop loop, RecordingHost host, PassResult? pass)
    {
        var failures = new List<string>();
        var moves = loop.Transcript.OfType<MoveObserved>().Select(m => m.Move).ToList();
        var written = moves.Select(EvaluationRunner.Written).ToList();

        if (pass?.Error is { } err && e.Completes != false) failures.Add($"The pass failed: {err}.");
        if (e.Completes == false && pass?.Error is null) failures.Add("Expected the pass to fail; it finished clean.");

        if (e.FirstMove is { } first && (moves.Count == 0 || !EvaluationRunner.Matches(moves[0], first)))
            failures.Add($"Expected the first move to be {first}; it was {(moves.Count == 0 ? "nothing" : written[0])}.");
        if (e.Moves is { Count: > 0 } wanted)
        {
            var i = 0;
            foreach (var move in moves) { if (i < wanted.Count && EvaluationRunner.Matches(move, wanted[i])) i++; }
            if (i < wanted.Count) failures.Add($"Expected the moves [{string.Join(", ", wanted)}] in order; missing {wanted[i]} in [{string.Join(", ", written)}].");
        }
        foreach (var forbidden in e.ForbiddenMoves ?? [])
            if (moves.Any(m => EvaluationRunner.Matches(m, forbidden))) failures.Add($"The move {forbidden} must not be made here.");

        if (e.Raises is { Count: > 0 } raiseKinds)
        {
            var kinds = host.Raised.Select(r => r.Kind).ToList();
            var i = 0;
            foreach (var kind in kinds) { if (i < raiseKinds.Count && string.Equals(kind, raiseKinds[i], StringComparison.OrdinalIgnoreCase)) i++; }
            if (i < raiseKinds.Count)
                failures.Add($"Expected raises [{string.Join(", ", raiseKinds)}] in order; missing {raiseKinds[i]} in [{string.Join(", ", kinds)}].");
        }
        foreach (var forbidden in e.ForbiddenRaises ?? [])
            if (host.Raised.Any(r => string.Equals(r.Kind, forbidden, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"A raise of kind '{forbidden}' must not be made here.");
        foreach (var fragment in e.RaiseContains ?? [])
            if (!host.Raised.Any(r => r.Objective.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"No raised objective mentions '{fragment}'.");

        if (e.MaxSteps is { } max && moves.Count > max) failures.Add($"Took {moves.Count} moves; at most {max} were allowed.");
        return failures;
    }

    private static string Observe(ObservingLoop loop, RecordingHost host, PassResult? pass)
    {
        var sb = new StringBuilder();
        sb.Append("moves: ").Append(string.Join(" → ", loop.Transcript.OfType<MoveObserved>().Select(m => EvaluationRunner.Written(m.Move))));
        sb.Append("; raises: ").Append(string.Join(" → ", host.Raised.Select(r => r.Kind + ":" + Observation.Clip(r.Objective, 80))));
        if (host.Refusals.Count > 0) sb.Append("; refused: ").Append(string.Join(" | ", host.Refusals.Select(r => Observation.Clip(r, 100))));
        if (pass is not null) sb.Append("; pass moves=").Append(pass.Moves).Append(" raises=").Append(pass.Raises).Append(pass.Error is null ? "" : " error=" + pass.Error);
        return sb.ToString();
    }

    /// <summary>Records raises and tool calls without starting tasks — the evaluation stand-in for the coordinator's observe host.</summary>
    public sealed class RecordingHost : IObservingHost
    {
        private readonly DateTimeOffset _at;
        public RecordingHost(DateTimeOffset at) => _at = at;
        public List<RaiseMove> Raised { get; } = new();
        public List<string> Refusals { get; } = new();
        public List<UseToolMove> Tools { get; } = new();

        public void Stepped(ObservingLoop loop, MindStep step) { }
        public void Waited(ObservingLoop loop, WaitMove move) { }
        public void Said(ObservingLoop loop, SayMove move) { }
        public void Refused(ObservingLoop loop, RaiseMove move, string reason) => Refusals.Add(reason);

        public Task<MoveOutcome> UseToolAsync(ObservingLoop loop, UseToolMove move, CancellationToken cancellationToken)
        {
            Tools.Add(move);
            return Task.FromResult(MoveOutcome.Of(new ToolObserved(_at, move.Tool, move.Args, true, "ok", null, [])));
        }

        public Task<MoveOutcome> RaiseAsync(ObservingLoop loop, RaiseMove move, IReadOnlyList<WindowLine> lines, DecisionRecord raise, CancellationToken cancellationToken)
        {
            Raised.Add(move);
            return Task.FromResult(MoveOutcome.Of(new RaisedObserved(_at, "eval-task-" + Raised.Count, move.Kind, move.Objective, "eval-excerpt-" + Raised.Count)));
        }
    }

    private sealed class FixedClock : IClock
    {
        private readonly Func<DateTimeOffset> _now;
        public FixedClock(Func<DateTimeOffset> now) => _now = now;
        public DateTimeOffset UtcNow => _now();
    }
}
