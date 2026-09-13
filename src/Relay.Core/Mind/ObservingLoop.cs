using Relay.Core.Decisions;
using Relay.Core.Time;

namespace Relay.Core.Mind;

/// <summary>What one listening pass may spend. A pass is cheap by construction: the slow ingest decides how often one runs at all.</summary>
public sealed record ObservingBudget(int MaxMovesPerPass = 3, int MaxToolCallsPerPass = 2, int MaxRaisesPerPass = 2, int MaxTranscript = 24);

/// <summary>What one pass came to, for the ledger and the listening view.</summary>
public sealed record PassResult(int Moves, int ToolCalls, int Raises, int PromptTokens, int CompletionTokens, long ElapsedMs, string? Error);

/// <summary>
/// The owner of consequences for a listening pass. The observing loop performs nothing itself: the host
/// runs the read-only tool, keeps the excerpt and raises the task.
/// </summary>
public interface IObservingHost
{
    /// <summary>The mind answered (or failed to). Called before the move is performed: for the listening view and the ledger.</summary>
    void Stepped(ObservingLoop loop, MindStep step);
    void Waited(ObservingLoop loop, WaitMove move);
    void Said(ObservingLoop loop, SayMove move);
    Task<MoveOutcome> UseToolAsync(ObservingLoop loop, UseToolMove move, CancellationToken cancellationToken);
    /// <summary>Keep the named lines and start a task for the objective. <paramref name="raise"/> carries the significance decision that let it through.</summary>
    Task<MoveOutcome> RaiseAsync(ObservingLoop loop, RaiseMove move, IReadOnlyList<WindowLine> lines, DecisionRecord raise, CancellationToken cancellationToken);
    /// <summary>A raise the loop turned down. Recorded so the listening view can say why nothing came of it.</summary>
    void Refused(ObservingLoop loop, RaiseMove move, string reason);
}

/// <summary>
/// The mind while Relay is listening. Not a task loop: there is no objective, no budget to finish within and
/// no end — one pass per stretch of conversation, each pass a few steps over the same rolling transcript, and
/// the usual answer is that the talk needs nothing. It never waits on anything: work that matters is raised as
/// a task with its own <see cref="TaskLoop"/>, its own budget and its own approvals, and this loop goes straight
/// back to listening. That is what keeps observation running while whatever it raised is in flight.
/// </summary>
public sealed class ObservingLoop
{
    private readonly IMind _mind;
    private readonly IObservingHost _host;
    private readonly MindContext _context;
    private readonly Decider _decider;
    private readonly ObservingBudget _budget;
    private readonly IClock _clock;
    private readonly List<Observation> _transcript = new();
    private readonly List<string> _raised = new();
    private bool _passing;

    public ObservingLoop(string streamId, IMind mind, IObservingHost host, MindContext context, Decider decider, ObservingBudget? budget = null, IClock? clock = null)
    {
        StreamId = streamId;
        _mind = mind;
        _host = host;
        _context = context;
        _decider = decider;
        _budget = budget ?? new ObservingBudget();
        _clock = clock ?? new SystemClock();
    }

    public string StreamId { get; }
    public string MindName => _mind.Name;
    public MindContext Context => _context;
    public IReadOnlyList<Observation> Transcript => _transcript;
    /// <summary>The objectives raised in this conversation, so the same thing is not raised twice.</summary>
    public IReadOnlyList<string> Raised => _raised;
    public int Passes { get; private set; }
    public int Moves { get; private set; }
    public int Raises { get; private set; }
    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public MindRead? LastRead { get; private set; }
    public IReadOnlyList<DecisionRecord> Decisions => _decider.Made;

    /// <summary>Optional local-inference gate shared with task loops (one StepAsync at a time on the machine).</summary>
    public Func<CancellationToken, Task>? AcquireInference { get; set; }
    public Action? ReleaseInference { get; set; }

    /// <summary>
    /// One pass over a stretch of conversation. Returns when the mind waits, says its line, spends the pass's moves,
    /// or fails past the retry decision. Never call concurrently; a stream runs one pass at a time.
    /// </summary>
    public async Task<PassResult> ObserveAsync(WindowObserved window, CancellationToken cancellationToken)
    {
        if (_passing) throw new InvalidOperationException("A pass is already running.");
        _passing = true;
        var started = _clock.UtcNow;
        Passes++;
        _transcript.Add(window);
        Trim();
        int moves = 0, tools = 0, raises = 0, promptTokens = 0, completionTokens = 0, failures = 0;
        try
        {
            while (moves < _budget.MaxMovesPerPass)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new MindRequest(StreamId, MindRequest.WindowOrigin, _transcript.ToList(), _context, _clock.UtcNow, moves, _budget.MaxMovesPerPass, Observing: true);
                MindStep step;
                var held = false;
                if (AcquireInference is not null) { await AcquireInference(cancellationToken).ConfigureAwait(false); held = true; }
                try { step = await _mind.StepAsync(request, cancellationToken).ConfigureAwait(false); }
                finally { if (held) ReleaseInference?.Invoke(); }
                promptTokens += step.PromptTokens;
                completionTokens += step.CompletionTokens;
                var now = _clock.UtcNow;

                if (!step.Ok)
                {
                    failures++;
                    var kind = step.Error!.StartsWith("Contract", StringComparison.Ordinal) ? "contract" : "model";
                    var retry = _decider.RetryFor(failures, kind);
                    _host.Stepped(this, step);
                    if (retry.Outcome == Decider.DoRetry)
                    {
                        var detail = kind == "contract" ? step.Error!["Contract:".Length..].Trim() : step.Error!;
                        Add(new SystemObserved(now, kind == "contract"
                            ? $"Your last reply was not usable: {detail}. Reply with one JSON object matching the contract."
                            : $"The model call failed ({detail}); trying again."));
                        continue;
                    }
                    // A failed pass is not a failed conversation: the next stretch of talk gets a clean pass.
                    Add(new SystemObserved(now, "That pass could not be completed; listening continues."));
                    return Finish(moves, tools, raises, promptTokens, completionTokens, started, step.Error);
                }

                failures = 0;
                moves++;
                Moves++;
                if (step.Read is not null) LastRead = step.Read;
                var move = step.Move!;
                Add(new MoveObserved(now, move, step.Feed));
                _host.Stepped(this, step);

                switch (move)
                {
                    case WaitMove wait:
                        _host.Waited(this, wait);
                        return Finish(moves, tools, raises, promptTokens, completionTokens, started, null);

                    case SayMove say:
                        // A line to the user is the end of the pass either way: nothing here is owed an answer, so there is nothing to continue towards.
                        _host.Said(this, say);
                        return Finish(moves, tools, raises, promptTokens, completionTokens, started, null);

                    case UseToolMove tool:
                    {
                        if (tools >= _budget.MaxToolCallsPerPass)
                        {
                            Add(new SystemObserved(now, $"The {_budget.MaxToolCallsPerPass} checks this pass allows are spent. Raise what matters from what you have, or wait."));
                            continue;
                        }
                        if (!_context.Tools.Any(t => t.Name == tool.Tool))
                        {
                            Add(new SystemObserved(now, $"There is no tool named '{tool.Tool}'. Tools: {string.Join(", ", _context.Tools.Select(t => t.Name))}."));
                            continue;
                        }
                        var earlier = _transcript.OfType<ToolObserved>().LastOrDefault(t => t.Tool == tool.Tool && SameArgs(t.Args, tool.Args));
                        if (earlier is not null)
                        {
                            Add(new SystemObserved(now, $"You already called {tool.Tool} with these arguments in this conversation; it returned: {Observation.Clip(earlier.Summary, 200)}. " +
                                "Calling it again changes nothing — raise what matters, or wait."));
                            continue;
                        }
                        tools++;
                        var outcome = await _host.UseToolAsync(this, tool, cancellationToken).ConfigureAwait(false);
                        foreach (var observation in outcome.Observations) Add(observation);
                        continue;
                    }

                    case RaiseMove raise:
                    {
                        var refusal = RefuseRaise(raise, raises);
                        if (refusal is not null) { Refuse(now, raise, refusal); continue; }
                        var decision = _decider.RaiseFor(step.Read);
                        if (decision.Outcome != Decider.RaiseWork)
                        {
                            Refuse(now, raise, $"you rated this {decision.Rationale}, which is below the bar for spending the user's attention. " +
                                "Wait; if it matters more than you read it, you will hear it again.");
                            continue;
                        }
                        var lines = Resolve(raise);
                        if (lines.Count == 0 && raise.Note is null)
                        {
                            Refuse(now, raise, $"segments {(raise.Segments.Count == 0 ? "(none given)" : string.Join(", ", raise.Segments))} name no line of this conversation. " +
                                "Name the labels shown above (#1, #2, …) for the lines that substantiate it, or carry the words yourself in note.");
                            continue;
                        }
                        raises++;
                        Raises++;
                        _raised.Add(Key(raise.Objective));
                        var outcome = await _host.RaiseAsync(this, raise, lines, decision, cancellationToken).ConfigureAwait(false);
                        foreach (var observation in outcome.Observations) Add(observation);
                        // One thing raised is usually the whole of a pass; a second is allowed for a window that held two, and then the pass ends.
                        if (raises >= _budget.MaxRaisesPerPass) return Finish(moves, tools, raises, promptTokens, completionTokens, started, null);
                        continue;
                    }

                    default:
                        Add(new SystemObserved(now, $"'{move.Type}' is not a move you may make while listening: you are not in this conversation and nothing is owed. " +
                            "Raise the work (move: raise) and the task will propose, delegate, build or ask the user as it needs to — or wait."));
                        continue;
                }
            }
            Add(new SystemObserved(_clock.UtcNow, $"That pass spent its {_budget.MaxMovesPerPass} moves without settling on anything; listening continues."));
            return Finish(moves, tools, raises, promptTokens, completionTokens, started, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(moves, tools, raises, promptTokens, completionTokens, started, "the pass was cancelled");
        }
        finally
        {
            _passing = false;
        }
    }

    /// <summary>Nothing was raised, and the mind is told why in the same transcript it will read on its next move.</summary>
    private void Refuse(DateTimeOffset now, RaiseMove raise, string reason)
    {
        Add(new RaisedObserved(now, null, raise.Kind, raise.Objective, null, reason));
        _host.Refused(this, raise, reason);
    }

    /// <summary>Deterministic refusals, checked before the significance decision: the mind is told which one it met.</summary>
    private string? RefuseRaise(RaiseMove raise, int raisedThisPass)
    {
        if (raise.Objective.Trim().Length < 8) return "the objective is too short to act on: say in one line what Relay should do about it.";
        if (raisedThisPass >= _budget.MaxRaisesPerPass) return $"{_budget.MaxRaisesPerPass} pieces of work are already raised from this pass; anything further waits for the next one.";
        var key = Key(raise.Objective);
        if (_raised.Contains(key, StringComparer.Ordinal))
            return "work with this objective was already raised in this conversation and is running or finished. Raising it again would duplicate it — wait, or raise something different.";
        return null;
    }

    /// <summary>The window lines a raise named, by the labels the transcript showed. A label that was never shown resolves to nothing.</summary>
    private IReadOnlyList<WindowLine> Resolve(RaiseMove raise)
    {
        var shown = _transcript.OfType<WindowObserved>().SelectMany(w => w.Lines)
            .GroupBy(l => l.Label, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        if (raise.Segments.Count == 0)
        {
            // No label and a note to keep: the latest line is what was heard, and it is what the excerpt is anchored to.
            var latest = _transcript.OfType<WindowObserved>().LastOrDefault()?.Fresh.LastOrDefault();
            return latest is null ? [] : [latest];
        }
        return raise.Segments.Select(label => shown.GetValueOrDefault(label)).OfType<WindowLine>().ToList();
    }

    private void Add(Observation observation)
    {
        _transcript.Add(observation);
        Trim();
    }

    /// <summary>
    /// The transcript is a window, not a record: a conversation runs for an hour and the ledger keeps what happened.
    /// The oldest observations go first, so the mind always sees the latest talk and what it most recently did about it.
    /// </summary>
    private void Trim()
    {
        if (_transcript.Count <= _budget.MaxTranscript) return;
        _transcript.RemoveRange(0, _transcript.Count - _budget.MaxTranscript);
    }

    private PassResult Finish(int moves, int tools, int raises, int promptTokens, int completionTokens, DateTimeOffset started, string? error)
    {
        PromptTokens += promptTokens;
        CompletionTokens += completionTokens;
        return new PassResult(moves, tools, raises, promptTokens, completionTokens, (long)(_clock.UtcNow - started).TotalMilliseconds, error);
    }

    private static string Key(string objective) => string.Join(' ', objective.ToLowerInvariant().Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool SameArgs(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var other) || !string.Equals(value.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
