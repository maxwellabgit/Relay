using Relay.Core.Decisions;
using Relay.Core.Time;

namespace Relay.Core.Mind;

public enum LoopStatus { Running, Waiting, Done, Failed }

/// <summary>What the loop can be waiting for when a move's consequence is not immediate.</summary>
public static class Waits
{
    public const string Approval = "approval";
    public const string User = "user";
    public const string Delegate = "delegate";
    public const string Build = "build";
    public const string Execution = "execution";
}

public sealed record LoopBudget(int MaxSteps = 12, int MaxToolCalls = 8, int MaxConsecutiveSays = 2);

/// <summary>
/// What the host reports after performing a move: the observations to append, and whether the loop must
/// now wait (and for what). An empty outcome means the move had no consequence worth recording.
/// </summary>
public sealed record MoveOutcome(IReadOnlyList<Observation> Observations, string? WaitFor = null)
{
    public static readonly MoveOutcome Nothing = new([]);
    public static MoveOutcome Of(params Observation[] observations) => new(observations);
    public static MoveOutcome Wait(string what, params Observation[] observations) => new(observations, what);
}

public sealed record LoopResult(LoopStatus Status, string Outcome, string? Answer, string Summary, IReadOnlyList<string> Feed, int Steps, int ToolCalls, string? Error);

/// <summary>
/// The owner of consequences. The loop decides nothing about the world; it hands each move to the host,
/// which runs the tool, puts the proposal to policy and the user, sends the package, builds the tool,
/// and answers with what happened. Calls arrive one at a time, in transcript order.
/// </summary>
public interface ILoopHost
{
    /// <summary>The mind answered (or failed to). Called before the move is performed: for the feed and the ledger.</summary>
    void Stepped(TaskLoop loop, MindStep step);
    void Said(TaskLoop loop, SayMove move);
    Task<MoveOutcome> UseToolAsync(TaskLoop loop, UseToolMove move, CancellationToken cancellationToken);
    /// <summary><paramref name="fof"/> is the fundamental-operation decision when the action changes Relay itself or was not asked for; null otherwise.</summary>
    Task<MoveOutcome> ProposeAsync(TaskLoop loop, ProposeMove move, DecisionRecord? fof, CancellationToken cancellationToken);
    Task<MoveOutcome> DelegateAsync(TaskLoop loop, DelegateMove move, CancellationToken cancellationToken);
    Task<MoveOutcome> BuildAsync(TaskLoop loop, BuildMove move, DecisionRecord fof, CancellationToken cancellationToken);
    Task<MoveOutcome> AskUserAsync(TaskLoop loop, AskUserMove move, CancellationToken cancellationToken);
    /// <summary>Cancel what the loop is waiting for.</summary>
    Task<MoveOutcome> StopAsync(TaskLoop loop, StopMove move, string waitingFor, CancellationToken cancellationToken);
    void Waiting(TaskLoop loop, string waitingFor);
    void Ended(TaskLoop loop, LoopResult result);
}

/// <summary>
/// The self-observing loop that runs one task. Each turn: show the mind the transcript, take its one
/// move, let the host perform it, append what happened, repeat. The loop stops stepping when a
/// consequence is not immediate (an approval, a question to the user, a delegate or a build in flight)
/// and is resumed by the host with the observation that ends the wait; while something is in flight, each
/// resume (a streamed partial, say) buys the mind exactly one step so it can narrate, keep waiting, or
/// stop. Budgets and the retry decision end the task visibly when the mind does not. Everything the
/// engine decides between paths goes through the <see cref="Decider"/> and is on record.
/// </summary>
public sealed class TaskLoop
{
    private readonly IMind _mind;
    private readonly ILoopHost _host;
    private readonly MindContext _context;
    private readonly Decider _decider;
    private readonly LoopBudget _budget;
    private readonly IClock _clock;
    private readonly List<Observation> _transcript = new();
    private readonly List<string> _feed = new();
    private int _failures;
    private int _consecutiveSays;
    private string? _lastSay;
    private bool _stepping;

    public TaskLoop(string taskId, string origin, IMind mind, ILoopHost host, MindContext context, Decider decider, LoopBudget? budget = null, IClock? clock = null)
    {
        TaskId = taskId;
        Origin = origin;
        _mind = mind;
        _host = host;
        _context = context;
        _decider = decider;
        _budget = budget ?? new LoopBudget();
        _clock = clock ?? new SystemClock();
    }

    public string TaskId { get; }
    /// <summary>ask | heard | follow_up (see <see cref="InputObserved"/>).</summary>
    public string Origin { get; }
    public string MindName => _mind.Name;
    public MindContext Context => _context;
    public LoopStatus Status { get; private set; } = LoopStatus.Running;
    public string? WaitingFor { get; private set; }
    public IReadOnlyList<Observation> Transcript => _transcript;
    public IReadOnlyList<string> Feed => _feed;

    /// <summary>
    /// Deterministic code adds lines of its own to the feed under the mind's sentences: the digest of a delegate's reply, for one.
    /// Marked with a leading "· " so the feed shows whose words they are.
    /// </summary>
    public void Annotate(IEnumerable<string> lines)
    {
        foreach (var line in lines) if (!string.IsNullOrWhiteSpace(line)) _feed.Add("· " + line.Trim());
    }
    public int Steps { get; private set; }
    public int ToolCalls { get; private set; }
    public int Proposals { get; private set; }
    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    /// <summary>The mind's most recent read; the first one decided the route.</summary>
    public MindRead? LastRead { get; private set; }
    /// <summary>The read of the first step: how the mind sized the task before doing anything.</summary>
    public MindRead? FirstRead { get; private set; }
    public DecisionRecord? Route { get; private set; }
    public string? Answer { get; private set; }
    public LoopResult? Result { get; private set; }
    public IReadOnlyList<DecisionRecord> Decisions => _decider.Made;

    /// <summary>Appends an observation without stepping (the task's input, a note from the engine).</summary>
    public void Observe(Observation observation) => _transcript.Add(observation);

    /// <summary>
    /// Steps until the loop waits or ends. Returns the result when the task ended, null when it is waiting
    /// for the host. Never call concurrently; the loop is single-consumer.
    /// </summary>
    public async Task<LoopResult?> RunAsync(CancellationToken cancellationToken)
    {
        if (_stepping) throw new InvalidOperationException("The loop is already stepping.");
        if (Status is LoopStatus.Done or LoopStatus.Failed) return Result;
        _stepping = true;
        try
        {
            Status = LoopStatus.Running;
            while (Status == LoopStatus.Running)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Steps >= _budget.MaxSteps) return End(LoopStatus.Failed, "step_budget", $"The mind did not finish within {_budget.MaxSteps} steps.");

                var request = new MindRequest(TaskId, Origin, _transcript.ToList(), _context, _clock.UtcNow, Steps, _budget.MaxSteps);
                var step = await _mind.StepAsync(request, cancellationToken).ConfigureAwait(false);
                PromptTokens += step.PromptTokens;
                CompletionTokens += step.CompletionTokens;
                var now = _clock.UtcNow;

                if (!step.Ok)
                {
                    _failures++;
                    var kind = step.Error!.StartsWith("Contract", StringComparison.Ordinal) ? "contract" : "model";
                    var retry = _decider.RetryFor(_failures, kind);
                    _host.Stepped(this, step);
                    if (retry.Outcome == Decider.DoRetry)
                    {
                        var detail = kind == "contract" ? step.Error!["Contract:".Length..].Trim() : step.Error!;
                        _transcript.Add(new SystemObserved(now, kind == "contract"
                            ? $"Your last reply was not usable: {detail}. Reply with one JSON object matching the contract."
                            : $"The model call failed ({detail}); trying again."));
                        continue;
                    }
                    return End(LoopStatus.Failed, "mind_failed", step.Error);
                }

                _failures = 0;
                Steps++;
                var move = step.Move!;
                if (step.Read is not null) { LastRead = step.Read; FirstRead ??= step.Read; }
                _transcript.Add(new MoveObserved(now, move, step.Feed));
                _feed.Add(step.Feed);
                _host.Stepped(this, step);

                if (Route is null && step.Read is not null)
                {
                    Route = _decider.RouteFor(step.Read, _context.DelegateProfiles.Count > 0, _context.CanBuild);
                    if (Route.Outcome != Decider.Local) _transcript.Add(new SystemObserved(now, RouteHint(Route)));
                }

                // Narration and waiting never leave the step; they end the task, or hand it back to the host.
                if (move is SayMove say)
                {
                    _host.Said(this, say);
                    if (say.Done) { Answer = say.Text; return End(LoopStatus.Done, "answered", null); }
                    if (WaitingFor is not null) return Pause();
                    _consecutiveSays++;
                    // The same sentence twice with done=false is a stall, not narration: the mind is told what it is doing and what would end the task.
                    var repeated = _lastSay is not null && string.Equals(_lastSay.Trim(), say.Text.Trim(), StringComparison.OrdinalIgnoreCase);
                    _lastSay = say.Text;
                    if (repeated)
                        _transcript.Add(new SystemObserved(now, "You said exactly that already; saying it again does nothing and spends a step. " +
                            "If that sentence is your answer, say it with done=true. If you need something from the user, use ask_user. Otherwise take a different move."));
                    else if (_consecutiveSays >= _budget.MaxConsecutiveSays)
                        _transcript.Add(new SystemObserved(now, "You have narrated without acting. Take a move now, or finish with say and done=true."));
                    continue;
                }
                if (move is WaitMove)
                {
                    if (WaitingFor is not null) return Pause();
                    return End(LoopStatus.Done, "waited", null);
                }
                _consecutiveSays = 0;

                var outcome = await PerformAsync(move, now, cancellationToken).ConfigureAwait(false);
                foreach (var observation in outcome.Observations) _transcript.Add(observation);
                if (outcome.WaitFor is not null) WaitingFor = outcome.WaitFor;
                if (WaitingFor is not null) return Pause();
            }
            return Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return End(LoopStatus.Failed, "cancelled", "The task was cancelled.");
        }
        finally
        {
            _stepping = false;
        }
    }

    /// <summary>
    /// The host reports what happened while the loop waited, and the loop steps again. A null
    /// <see cref="MoveOutcome.WaitFor"/> ends the wait; a non-null one (a streamed partial) keeps it, so the
    /// mind gets exactly one step before the loop waits again.
    /// </summary>
    public Task<LoopResult?> ResumeAsync(MoveOutcome outcome, CancellationToken cancellationToken)
    {
        if (Status is LoopStatus.Done or LoopStatus.Failed) return Task.FromResult(Result);
        foreach (var observation in outcome.Observations) _transcript.Add(observation);
        WaitingFor = outcome.WaitFor;
        return RunAsync(cancellationToken);
    }

    private async Task<MoveOutcome> PerformAsync(Move move, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (move)
        {
            case UseToolMove tool:
            {
                if (ToolCalls >= _budget.MaxToolCalls)
                    return MoveOutcome.Of(new SystemObserved(now, $"The tool budget of {_budget.MaxToolCalls} calls is spent. Finish with what you have" +
                        (_context.DelegateProfiles.Count > 0 ? ", or, if a delegate could settle what the tools could not, delegate now: the user is asked and decides." : ".")));
                if (!_context.Tools.Any(t => t.Name == tool.Tool))
                {
                    var nearest = _context.Tools.FirstOrDefault(t => tool.Tool.Contains(t.Name, StringComparison.Ordinal) || t.Name.Contains(tool.Tool, StringComparison.Ordinal));
                    var hint = nearest is null ? "" : $" Did you mean {nearest.Name}({string.Join(", ", nearest.Arguments)})? Use exactly that name.";
                    return MoveOutcome.Of(new SystemObserved(now, $"There is no tool named '{tool.Tool}'. Tools: {string.Join(", ", _context.Tools.Select(t => t.Name))}.{hint}"));
                }
                // The same call again would return the same thing: the loop observes the repetition instead of spending a call on it.
                var earlier = _transcript.OfType<ToolObserved>().LastOrDefault(t => t.Tool == tool.Tool && SameArgs(t.Args, tool.Args));
                if (earlier is not null)
                    return MoveOutcome.Of(new SystemObserved(now, $"You already called {tool.Tool} with these arguments; it returned: {Observation.Clip(earlier.Summary, 200)}. Calling it again changes nothing. " +
                        "Try different arguments, use another tool, or, if no tool can produce what is needed, say so (needs: new_tool) and finish."));
                ToolCalls++;
                return await _host.UseToolAsync(this, tool, cancellationToken).ConfigureAwait(false);
            }

            case ProposeMove propose:
            {
                if (!_context.Actions.Any(a => a.Action == propose.Action))
                {
                    // A proposal named like a tool, with inputs and outputs, is a build that took the wrong move; say so rather than only listing the actions.
                    var meantBuild = _context.CanBuild && (LastRead?.Has(MindRead.NeedNewTool) == true || propose.Target.ContainsKey("inputs") || propose.Target.ContainsKey("outputs") || propose.Target.ContainsKey("tool_name"));
                    // "propose research" (seen live) is a delegation that took the wrong move: a profile name, or a research/external need, says so.
                    var meantDelegate = !meantBuild && _context.DelegateProfiles.Count > 0 && (_context.DelegateProfiles.Contains(propose.Action, StringComparer.OrdinalIgnoreCase)
                        || propose.Action.ToLowerInvariant() is "research" or "delegate" or "external" or "ask_external" or "model_request" or "model.request"
                        || LastRead?.Has(MindRead.NeedExternalReasoning) == true);
                    var hint = meantBuild ? $" A new tool is not proposed, it is built: use the build move with name={propose.Target.GetValueOrDefault("tool_name") ?? propose.Action} and args inputs/outputs."
                        : meantDelegate ? $" An external AI is not proposed, it is delegated to: use the delegate move with name=one of {string.Join("/", _context.DelegateProfiles)} and text=the complete prompt you write for it."
                        : "";
                    return MoveOutcome.Of(new SystemObserved(now, $"'{propose.Action}' is not an action you may propose here. Actions: {string.Join(", ", _context.Actions.Select(a => a.Action))}.{hint}"));
                }
                var selfChange = IsSelfChange(propose.Action);
                var selfDirected = Origin != InputObserved.Ask;
                var fof = selfChange || selfDirected ? _decider.FofFor(LastRead, Move.Propose, propose.Action, selfDirected) : null;
                if (fof?.Outcome == Decider.Refuse) return MoveOutcome.Of(new SystemObserved(now, $"Refused by the fundamental-operation flag: {fof.Rationale}. Take another path or explain to the user."));
                Proposals++;
                return await _host.ProposeAsync(this, propose, fof, cancellationToken).ConfigureAwait(false);
            }

            case DelegateMove delegateMove:
                if (_context.DelegateProfiles.Count == 0) return MoveOutcome.Of(new SystemObserved(now, "No delegate profile is configured; delegation is unavailable. Answer locally and say what is missing."));
                // A follow-up turn names the request it continues; the conversation's profile stands, so the name may be left out.
                if (delegateMove.ReplyTo is null && !_context.DelegateProfiles.Contains(delegateMove.Profile, StringComparer.Ordinal))
                    return MoveOutcome.Of(new SystemObserved(now, $"There is no delegate profile '{delegateMove.Profile}'. Profiles: {string.Join(", ", _context.DelegateProfiles)}."));
                // The same prompt again, as a fresh request, after a reply came back is a stall (seen live: the mind re-sent the ask it had just had
                // answered). The loop points at the reply instead of asking the user to approve the same package twice.
                if (delegateMove.ReplyTo is null)
                {
                    var sent = _transcript.OfType<MoveObserved>().Select(m => m.Move).OfType<DelegateMove>().Where(d => !ReferenceEquals(d, delegateMove))
                        .LastOrDefault(d => d.ReplyTo is null && string.Equals(d.Prompt.Trim(), delegateMove.Prompt.Trim(), StringComparison.OrdinalIgnoreCase));
                    var answered = sent is null ? null : _transcript.OfType<DelegateObserved>().LastOrDefault(d => d.Stage == DelegateObserved.Returned);
                    if (answered is not null)
                        return MoveOutcome.Of(new SystemObserved(now, $"You already delegated exactly this prompt; request {answered.RequestId} returned" +
                            (answered.Digest is { Count: > 0 } ? $": {Observation.Clip(string.Join(" / ", answered.Digest), 300)}" : $" ({answered.Chars} chars, artifact {answered.ArtifactId})") +
                            ". Sending it again would only ask the user to approve the same package twice. Answer from the reply with say and done=true" +
                            (answered.TurnsLeft > 0 ? $", or ask the delegate a follow-up with reply_to={answered.RequestId}." : ", or write a different prompt.")));
                }
                Proposals++;
                return await _host.DelegateAsync(this, delegateMove, cancellationToken).ConfigureAwait(false);

            case BuildMove build:
            {
                if (!_context.CanBuild) return MoveOutcome.Of(new BuildObserved(now, build.Name, BuildObserved.Unavailable, "Building tools is not available in this build. Tell the user which tool would be needed and answer what you can."));
                var fof = _decider.FofFor(LastRead, Move.Build, build.Name, selfDirected: true);
                if (fof.Outcome == Decider.Refuse) return MoveOutcome.Of(new SystemObserved(now, $"Refused by the fundamental-operation flag: {fof.Rationale}. Explain to the user instead."));
                Proposals++;
                return await _host.BuildAsync(this, build, fof, cancellationToken).ConfigureAwait(false);
            }

            case AskUserMove ask:
                return await _host.AskUserAsync(this, ask, cancellationToken).ConfigureAwait(false);

            case StopMove stop:
                if (WaitingFor is null) return MoveOutcome.Of(new SystemObserved(now, "Nothing is running that could be stopped."));
                var waited = WaitingFor;
                WaitingFor = null;
                return await _host.StopAsync(this, stop, waited, cancellationToken).ConfigureAwait(false);

            default:
                return MoveOutcome.Of(new SystemObserved(now, $"The move '{move.Type}' has no handler."));
        }
    }

    private LoopResult? Pause()
    {
        Status = LoopStatus.Waiting;
        _host.Waiting(this, WaitingFor!);
        return null;
    }

    private LoopResult End(LoopStatus status, string outcome, string? error)
    {
        Status = status;
        var summary = error ?? (_feed.Count > 0 ? _feed[^1] : outcome);
        Result = new LoopResult(status, outcome, Answer, summary, _feed.ToList(), Steps, ToolCalls, error);
        _host.Ended(this, Result);
        return Result;
    }

    public static bool IsSelfChange(string action) => action is Policy.Actions.UpdatePreference or Policy.Actions.UpdatePrompt or Policy.Actions.AddTool;

    private static bool SameArgs(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var other) || !string.Equals(value.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string RouteHint(DecisionRecord route) => route.Outcome switch
    {
        Decider.OfferBuild => $"Route: this needs a capability no tool has ({route.Rationale}). When you know the inputs and outputs the tool needs, use build.",
        Decider.OfferDelegate => $"Route: this looks too complex to settle locally ({route.Rationale}). Gather the local facts you can, then answer or use delegate with a prompt you write; the user decides whether it leaves the machine.",
        _ => $"Route: {route.Outcome} ({route.Rationale}).",
    };
}
