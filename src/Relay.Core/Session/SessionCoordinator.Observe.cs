using Relay.Core.Decisions;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Stream;
using Relay.Core.Tasks;

namespace Relay.Core.Session;

/// <summary>
/// Listening: the mind reads the conversation itself. One <see cref="ObservingLoop"/> per stream, one pass per
/// stretch of talk, the same moves and the same read the mind uses for everything else — and one move the loop
/// only has here, <c>raise</c>, which hands work to a task with its own loop, budget and approvals. The observing
/// loop never waits on anything, which is what lets observation continue while what it raised is in flight.
/// </summary>
public sealed partial class SessionCoordinator
{
    /// <summary>
    /// The conversation's loop. Built before the stream state exists, so the host is given the state after it is
    /// constructed — the loop cannot run until <see cref="ObservePass"/>, which only happens once the stream is set.
    /// </summary>
    private ObservingLoop NewObservingLoop(string streamId)
    {
        var host = new ObserveHost(this);
        // No actions: the observing loop proposes nothing. What a raised task can go on to do is stated in the prompt instead.
        var context = MindContextOf([], []);
        var decider = new Decider(_services.Decisions, RecordStreamDecision);
        return new ObservingLoop(streamId, _services.Mind!, host, context, decider,
            new ObservingBudget(_settings.Stream.MaxMovesPerPass, _settings.Stream.MaxToolCallsPerPass, _settings.Stream.MaxRaisesPerPass), _clock);
    }

    /// <summary>
    /// Every line of the held window, labelled. A label is assigned once per segment and kept for the life of the
    /// stream, so a raise may name a line the mind saw two passes ago and a label it was never shown names nothing.
    /// </summary>
    private static WindowObserved? WindowFor(StreamState stream, DateTimeOffset now)
    {
        var fresh = stream.Buffer.UnreadIds().ToHashSet(StringComparer.Ordinal);
        var freshLines = new List<WindowLine>();
        var earlierLines = new List<WindowLine>();
        foreach (var segment in stream.Buffer.Segments)
        {
            if (!stream.Labels.TryGetValue(segment.SegmentId, out var label))
            {
                label = "#" + ++stream.Labelled;
                stream.Labels[segment.SegmentId] = label;
            }
            (fresh.Contains(segment.SegmentId) ? freshLines : earlierLines).Add(new WindowLine(label, segment.SegmentId, segment.Text));
        }
        return freshLines.Count == 0 ? null : new WindowObserved(now, stream.StreamId, freshLines, earlierLines, stream.Buffer.HeldSeconds);
    }

    /// <summary>One pass of the mind over what has been said since the last one. Runs off the coordinator thread; every consequence comes back to it.</summary>
    private void ObservePass(StreamState stream, bool final)
    {
        var now = _clock.UtcNow;
        var window = WindowFor(stream, now);
        if (window is null)
        {
            if (final) CompleteStream(stream, "stopped");
            return;
        }
        var loop = stream.Loop;
        // The project list and the tools can both have changed since the stream opened (a task raised earlier created a project).
        loop.Context.Projects = _services.Registry.Active.Select(p => $"{p.Name} (id {p.Id}, slug {p.Slug})").ToList();
        loop.Context.Tools = _services.Tools?.AllDescriptors() ?? ToolBroker.Descriptors;
        stream.Reading = true;
        stream.Passes++;
        var fresh = window.Fresh.Select(l => l.SegmentId).ToList();
        var cts = new CancellationTokenSource();
        stream.PassCts = cts;
        stream.PassTimedOut = false;
        stream.PassTimeout = _scheduler.Schedule(TimeSpan.FromMilliseconds(_settings.Listening.PassTimeoutMs), () => { stream.PassTimedOut = true; cts.Cancel(); });
        Task<PassResult> passing;
        try { passing = loop.ObserveAsync(window, cts.Token); }
        catch (Exception ex) { passing = Task.FromException<PassResult>(ex); }
        passing.ContinueWith(t => _scheduler.Post(() => OnObserved(stream, window, fresh, t, final)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnObserved(StreamState stream, WindowObserved window, IReadOnlyList<string> fresh, Task<PassResult> passing, bool final)
    {
        if (_stream != stream) return;
        stream.PassTimeout?.Dispose();
        stream.PassTimeout = null;
        stream.PassCts?.Dispose();
        stream.PassCts = null;
        stream.Reading = false;
        stream.LastCheckAt = _clock.UtcNow;
        stream.Buffer.MarkRead(fresh);

        var timedOut = stream.PassTimedOut;
        stream.PassTimedOut = false;

        PassResult pass;
        if (passing.IsFaulted) pass = new PassResult(0, 0, 0, 0, 0, 0, passing.Exception?.GetBaseException().Message ?? "the pass failed");
        else if (passing.IsCanceled) pass = new PassResult(0, 0, 0, 0, 0, 0, TimedOut());
        else pass = passing.Result;
        // The loop reports a cancelled pass without knowing why; only the coordinator's clock does.
        if (timedOut && pass.Error is not null) pass = pass with { Error = TimedOut() };

        string TimedOut() => $"the pass timed out after {_settings.Listening.PassTimeoutMs} ms";

        if (pass.Error is not null)
        {
            stream.LastError = pass.Error;
            Append(EventTypes.ObserveFailed, new { streamId = stream.StreamId, mind = stream.Loop.MindName, segments = fresh, error = pass.Error, elapsedMs = pass.ElapsedMs });
        }
        else stream.LastError = null;

        // A pass that raised nothing is the usual outcome and says so with sizes only; a raise records itself as it happens.
        if (pass.Raises == 0)
            Append(EventTypes.ObserveChecked, new
            {
                streamId = stream.StreamId, mind = stream.Loop.MindName, segments = fresh, hashes = Hashes(stream, fresh), held = window.Earlier.Count + window.Fresh.Count,
                promptTokens = pass.PromptTokens, completionTokens = pass.CompletionTokens, elapsedMs = pass.ElapsedMs, moves = pass.Moves, toolCalls = pass.ToolCalls, belowThreshold = 0,
            });

        if (final || stream.Finishing) CompleteStream(stream, "stopped");
        Notify();
    }

    private static IReadOnlyList<string> Hashes(StreamState stream, IReadOnlyList<string> segmentIds)
        => segmentIds.Select(id => stream.Buffer.Find(id)?.Sha256[..16]).OfType<string>().ToList();

    // ----------------------------------------------------------------------------------------
    // Consequences, on the coordinator thread
    // ----------------------------------------------------------------------------------------

    private void OnObserveStepped(StreamState stream, ObservingLoop loop, MindStep step)
    {
        if (_stream != stream) return;
        Append(EventTypes.ModelRequested, new { streamId = stream.StreamId, host = "observe", model = loop.MindName, promptChars = step.PromptChars, sources = loop.Transcript.Count });
        Append(EventTypes.ModelResponded, new { streamId = stream.StreamId, ok = step.Ok, chars = step.Raw?.Length ?? 0, elapsedMs = step.ElapsedMs, error = step.Error, promptTokens = step.PromptTokens, completionTokens = step.CompletionTokens });
        if (!step.Ok)
        {
            // The raw reply can quote the room: it is fingerprinted here as an overheard task's would be.
            Append(EventTypes.MindFailed, new { streamId = stream.StreamId, pass = loop.Passes, error = step.Error, raw = step.Raw is null ? null : Fingerprint(step.Raw) });
            Notify();
            return;
        }
        var move = step.Move!;
        var read = step.Read;
        Append(EventTypes.ObserveStepped, new
        {
            streamId = stream.StreamId, mind = loop.MindName, pass = loop.Passes, move = move.Type, brief = Fingerprint(move.Brief()), feed = Fingerprint(step.Feed),
            read = read is null ? null : new { intent = Fingerprint(read.Intent), complexity = read.Complexity, needs = read.Needs, significance = read.Significance, sensitivity = read.Sensitivity },
            promptTokens = step.PromptTokens, completionTokens = step.CompletionTokens, elapsedMs = step.ElapsedMs,
        });
        Notify();
    }

    /// <summary>A raise the loop turned down. The objective would quote the room, so only the kind and the reason are recorded.</summary>
    private void OnObserveRefused(StreamState stream, ObservingLoop loop, RaiseMove move, string reason)
    {
        if (_stream != stream) return;
        Append(EventTypes.ObserveRaised, new { streamId = stream.StreamId, by = loop.MindName, kind = move.Kind, refused = reason, objective = Fingerprint(move.Objective), segments = move.Segments });
        Notify();
    }

    /// <summary>The mind's one line about what it heard. It goes to the listening view, not to a task: nothing here was asked of Relay.</summary>
    private void OnObserveSaid(StreamState stream, SayMove say)
    {
        if (_stream != stream) return;
        stream.LastSaid = say.Text;
        Notify();
    }

    private MoveOutcome OnObserveUseTool(StreamState stream, ObservingLoop loop, UseToolMove move)
    {
        var tools = new ToolBroker(ToolSources, new StreamSink(this, stream), int.MaxValue); // the pass's own budget governs
        var result = tools.Call(move.Tool, move.Args);
        var ids = result.Hits?.Select(h => h.Id).Distinct(StringComparer.Ordinal).ToList() ?? [];
        // The data of a tool result is the user's own notes, not the room's words, but it is not put in the ledger either way.
        string? data = null;
        if (result.Ok && result.Data is not null)
        {
            try { data = System.Text.Json.JsonSerializer.Serialize(result.Data, Storage.RelayJson.Compact); }
            catch (NotSupportedException) { data = null; }
        }
        return MoveOutcome.Of(new ToolObserved(_clock.UtcNow, move.Tool, move.Args, result.Ok, result.Ok ? result.Summary : result.Error ?? "failed", data, ids));
    }

    /// <summary>
    /// The one consequence a listening pass can have: the named lines are kept as an excerpt and a task is started
    /// for the objective. The task runs on its own from here — this returns straight away so the pass can end and
    /// the next stretch of conversation is heard.
    /// </summary>
    private MoveOutcome OnObserveRaise(StreamState stream, ObservingLoop loop, RaiseMove move, IReadOnlyList<WindowLine> lines, DecisionRecord raise)
    {
        var now = _clock.UtcNow;
        var segmentIds = lines.Select(l => l.SegmentId).Distinct(StringComparer.Ordinal).ToList();
        var kind = RaisedKind(move.Kind);
        var raised = Append(EventTypes.ObserveRaised, new
        {
            streamId = stream.StreamId, by = loop.MindName, kind = kind.Wire(), segments = segmentIds, hashes = Hashes(stream, segmentIds),
            objective = Fingerprint(move.Objective), why = move.Why.Length == 0 ? null : Fingerprint(move.Why), topic = move.Topic is null ? null : Fingerprint(move.Topic),
            noteChars = move.Note?.Length, noteType = move.NoteType, project = move.Project, significance = raise.Score,
        });
        if (raised is null) return MoveOutcome.Of(new RaisedObserved(now, null, move.Kind, move.Objective, null, "the ledger is locked; nothing can be raised."));
        var sourceEventId = raised.Id;

        Excerpt? excerpt = null;
        var elapsed = (now - stream.StartedAt).TotalSeconds;
        if (Excerpts.Existing(segmentIds) is { } shared) excerpt = shared;
        else if (segmentIds.Any(id => stream.Buffer.Find(id) is not null))
        {
            try
            {
                var guard = new RetentionGuard(Preferences.ExcerptMaxSeconds, Preferences.MaxRetainedFraction);
                excerpt = Excerpts.Build(stream.StreamId, segmentIds, move.Objective, loop.MindName, stream.Buffer, guard, elapsed, now);
                var path = Excerpts.Persist(excerpt);
                stream.Excerpts++;
                _services.Index.IndexExcerpt(excerpt);
                var stored = Append(EventTypes.ExcerptStored, new
                {
                    streamId = stream.StreamId, excerptId = excerpt.ExcerptId, triggerSegmentId = excerpt.TriggerSegmentId, segments = excerpt.Segments.Count,
                    referenced = excerpt.References.Sum(r => r.SegmentIds.Count), seconds = excerpt.Seconds, chars = excerpt.Text.Length, shrunkByGuard = excerpt.ShrunkByGuard,
                    retainedSeconds = Excerpts.RetainedSeconds, elapsedSeconds = elapsed, kind = kind.Wire(), why = Fingerprint(move.Why), path,
                });
                if (stored is not null) sourceEventId = stored.Id;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _notice = "An excerpt could not be stored: " + ex.Message;
                excerpt = null;
            }
        }

        stream.Tasks++;
        var task = StartRaisedTask(move, kind, excerpt, stream.StreamId, sourceEventId, loop.MindName, raise.Score);
        return MoveOutcome.Of(new RaisedObserved(now, task?.TaskId, move.Kind, move.Objective, excerpt?.ExcerptId,
            task is null ? "the task could not be started; the ledger refused it." : null));
    }

    /// <summary>A raise becomes an observed task with its own loop. A raise whose whole content is a note skips planning: the mind already wrote it.</summary>
    private TaskState? StartRaisedTask(RaiseMove move, TaskKind kind, Excerpt? excerpt, string streamId, string sourceEventId, string by, double significance)
    {
        var watched = move.Topic is not null && Preferences.WatchedTerms.Contains(move.Topic, StringComparer.OrdinalIgnoreCase) ? move.Topic : null;
        var task = NewTask(TaskOrigin.Observed, kind, "observed", streamId, sourceEventId, move.Objective, foreground: false,
            excerptId: excerpt?.ExcerptId, mergeKey: move.MergeKey, topic: move.Topic, projectHint: move.Project, title: Truncate(move.Objective, 80),
            why: move.Why.Length > 0 ? move.Why : "raised while listening", confidence: significance, watchedTerm: watched);
        if (Append(EventTypes.TaskCreated, TaskCreatedPayload(task, chars: move.Objective.Length, raisedBy: by)) is null)
        {
            _tasks.Remove(task);
            return null;
        }
        PersistTask(task);
        if (kind == TaskKind.Remember && !string.IsNullOrWhiteSpace(move.Note))
        {
            Remember(task, move.Note!, move.NoteType, move.Topic, move.Project, excerpt, Producers.Mind);
            return task;
        }
        if (!OrchestratorEnabled)
        {
            task.Plan = new TurnPlan(false, "Relay's mind is off; what was heard is on record and nothing was done with it.", [], null, [], [], Producers.Engine);
            FinishTask(task);
            return task;
        }
        BeginTask(task);
        return task;
    }

    private static TaskKind RaisedKind(string kind) => kind switch
    {
        "remember" => TaskKind.Remember,
        "check" => TaskKind.Check,
        "resolve" => TaskKind.Resolve,
        "organize" => TaskKind.Organize,
        "research" => TaskKind.Research,
        "improve" => TaskKind.Improve,
        _ => TaskKind.Answer,
    };

    /// <summary>A decision taken on a listening pass. It belongs to the conversation, not to a task, so it is recorded against the stream.</summary>
    private void RecordStreamDecision(DecisionRecord decision)
        => Append(EventTypes.DecisionMade, new { streamId = _stream?.StreamId, decision = decision.Name, outcome = decision.Outcome, score = decision.Score, features = decision.Features, weights = decision.Weights, rationale = decision.Rationale });

    // ----------------------------------------------------------------------------------------
    // The host the observing loop talks to: marshals every call onto the coordinator thread
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A tool call made on a listening pass. It belongs to no task, so it is recorded against the stream — and its
    /// arguments are the words just heard, so the ledger gets fingerprints of them exactly as an overheard task's do.
    /// </summary>
    private sealed class StreamSink : ITurnSink
    {
        private readonly SessionCoordinator _owner;
        private readonly StreamState _stream;

        public StreamSink(SessionCoordinator owner, StreamState stream) { _owner = owner; _stream = stream; }

        private void Record(string type, object data) => _owner._scheduler.Post(() =>
        {
            if (_owner._stream != _stream) return;
            _owner.Append(type, data);
            _owner.Notify();
        });

        public void Progress(string text) { }
        public void ToolCalled(string tool, IReadOnlyDictionary<string, string> args)
            => Record(EventTypes.ToolCalled, new { streamId = _stream.StreamId, tool, args = args.ToDictionary(kv => kv.Key, kv => Fingerprint(kv.Value), StringComparer.Ordinal) });
        public void ToolReturned(string tool, bool ok, string summary, int items)
            => Record(EventTypes.ToolReturned, new { streamId = _stream.StreamId, tool, ok, summary = Fingerprint(summary), items });
        public void ModelRequested(string host, string model, int promptChars, int sources) { }
        public void ModelResponded(bool ok, int chars, long elapsedMs, string? error, int promptTokens = 0, int completionTokens = 0) { }
    }

    /// <summary>
    /// The loop identifies its own stream: a consequence that arrives after the conversation ended, or while a
    /// different one is open, belongs to nothing and is dropped.
    /// </summary>
    private sealed class ObserveHost : IObservingHost
    {
        private readonly SessionCoordinator _owner;

        public ObserveHost(SessionCoordinator owner) => _owner = owner;

        private void OnCoordinator(ObservingLoop loop, Action<StreamState> consequence) => _owner._scheduler.Post(() =>
        {
            if (_owner.StreamOf(loop) is { } stream) consequence(stream);
        });

        private Task<MoveOutcome> OnCoordinator(ObservingLoop loop, Func<StreamState, MoveOutcome> consequence)
        {
            var tcs = new TaskCompletionSource<MoveOutcome>();
            _owner._scheduler.Post(() =>
            {
                try
                {
                    if (_owner.StreamOf(loop) is not { } stream) { tcs.SetCanceled(); return; }
                    tcs.SetResult(consequence(stream));
                    _owner.Notify();
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public void Stepped(ObservingLoop loop, MindStep step) => OnCoordinator(loop, stream => _owner.OnObserveStepped(stream, loop, step));
        public void Said(ObservingLoop loop, SayMove move) => OnCoordinator(loop, stream => _owner.OnObserveSaid(stream, move));
        public void Waited(ObservingLoop loop, WaitMove move) { /* the usual outcome; the pass record says the talk needed nothing */ }
        public void Refused(ObservingLoop loop, RaiseMove move, string reason) => OnCoordinator(loop, stream => _owner.OnObserveRefused(stream, loop, move, reason));
        public Task<MoveOutcome> UseToolAsync(ObservingLoop loop, UseToolMove move, CancellationToken cancellationToken) => OnCoordinator(loop, stream => _owner.OnObserveUseTool(stream, loop, move));
        public Task<MoveOutcome> RaiseAsync(ObservingLoop loop, RaiseMove move, IReadOnlyList<WindowLine> lines, DecisionRecord raise, CancellationToken cancellationToken)
            => OnCoordinator(loop, stream => _owner.OnObserveRaise(stream, loop, move, lines, raise));
    }

    private StreamState? StreamOf(ObservingLoop loop) => _stream is { } stream && stream.Loop == loop ? stream : null;
}
