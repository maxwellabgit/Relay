using System.Text;
using Relay.Core.Agents;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Judge;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Orchestration;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Tasks;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests.Support;

/// <summary>
/// Prompt-driven scenarios against the real coordinator, stores, judge, policy engine and executor.
/// Each step drives the same public surface the desktop UI uses (chords, surface text, the ask box,
/// approve, reject, edit, cancel, respond) and records a transcript that shows exactly how RELAY0
/// handled it: state, tasks, plans, tool calls, proposals, decisions, executions, attention, ledger lines.
/// </summary>
public sealed class Scenario : IDisposable
{
    private readonly TempRoot _tmp;
    private readonly IOrchestrator? _orchestrator;
    private readonly IWorkerHost? _workerHost;
    private readonly IJudge? _judge;
    private readonly Func<ExternalModelProfile, IModelClient>? _externalClients;
    private readonly bool _inlinePost;
    private readonly MemorySecretStore _secrets = new();
    private readonly StringBuilder _transcript = new();
    private readonly HashSet<string> _seenAttention = new(StringComparer.Ordinal);
    private Harness _h;
    private int _step;
    private long _activityFrom;
    private string _heard = "";

    private Scenario(TempRoot tmp, Action<RelaySettings>? configure, IOrchestrator? orchestrator, IWorkerHost? workerHost, FixedClock? clock, IJudge? judge, Func<ExternalModelProfile, IModelClient>? externalClients, bool inlinePost)
    {
        _tmp = tmp;
        _orchestrator = orchestrator;
        _workerHost = workerHost;
        _judge = judge;
        _externalClients = externalClients;
        _inlinePost = inlinePost;
        _h = Open(configure, clock);
        Log($"== Session started ({_h.Coordinator.SessionId[^8..]}) state {_h.Snap.State.Label()} judge {_h.Snap.JudgeName} ==");
    }

    /// <param name="inlinePost">False when background threads (external requests, real workers) post completions; the test then drains them with <see cref="PumpUntil"/>.</param>
    public static Scenario New(TempRoot tmp, Action<RelaySettings>? configure = null, IOrchestrator? orchestrator = null, IWorkerHost? workerHost = null, FixedClock? clock = null,
        IJudge? judge = null, Func<ExternalModelProfile, IModelClient>? externalClients = null, bool inlinePost = true)
        => new(tmp, configure, orchestrator, workerHost, clock, judge, externalClients, inlinePost);

    private Harness Open(Action<RelaySettings>? configure, FixedClock? clock)
        => new Harness(_tmp.Root, configure: configure, orchestrator: _orchestrator, workerHost: _workerHost, clock: clock, judge: _judge, externalClients: _externalClients, secrets: _secrets, inlinePost: _inlinePost).Start();

    /// <summary>Drains work posted by background threads on this thread until the condition holds; fails the scenario on timeout.</summary>
    public Scenario PumpUntil(string what, Func<bool> condition, TimeSpan? timeout = null)
    {
        Begin($"Pump until {what}");
        if (!_h.Scheduler.PumpUntil(condition, timeout ?? TimeSpan.FromSeconds(10))) throw Fail($"Timed out waiting for {what}");
        return End();
    }

    public Harness H => _h;
    public SessionCoordinator C => _h.Coordinator;
    public RelaySnapshot Snap => _h.Snap;
    public RelaySettings Settings => _h.SettingsLoad.Settings;
    /// <summary>The most recent foreground task (command capture, direct ask from idle, or Projects-panel operation).</summary>
    public TaskView Response => Snap.Response ?? throw new InvalidOperationException("No task response.");
    public string WorkspacePath { get; private set; } = "";

    /// <summary>Creates a folder beside the data root and registers it as a workspace root.</summary>
    public Scenario WithWorkspace(string name = "ws")
    {
        WorkspacePath = Path.Combine(Path.GetDirectoryName(_tmp.Root.Path)!, Path.GetFileName(_tmp.Root.Path) + "-" + name);
        Directory.CreateDirectory(WorkspacePath);
        var ok = C.RegisterWorkspace(WorkspacePath, name);
        Log($"-- register workspace {WorkspacePath}: {(ok ? "ok" : "FAILED: " + Snap.Notice)}");
        return this;
    }

    /// <summary>Stores an API key under the secret name a model or external profile uses.</summary>
    public Scenario WithSecret(string name, string value = "test-key")
    {
        _secrets.Set(name, value);
        return this;
    }

    /// <summary>
    /// Turns the judge on through the settings path the UI uses, so the note chord listens from here on.
    /// The judge instance itself is whatever the scenario was built with (a scripted one, usually).
    /// </summary>
    public Scenario WithListening(string mode = JudgeSettings.Heuristic)
    {
        var problems = C.UpdateSettings(x => x.Judge.Mode = mode);
        if (problems.Count > 0) throw Fail("Listening could not be enabled: " + string.Join(" ", problems));
        Log($"-- judge mode {mode}: the note chord now listens");
        return this;
    }

    // ----------------------------------------------------------------------------------------
    // Direct input: the chords and the ask box
    // ----------------------------------------------------------------------------------------

    /// <summary>Note chord with the judge off: a dictated note, organized when it settles.</summary>
    public Scenario Note(string text) => Capture(CaptureMode.Note, text);
    /// <summary>Command chord: a direct instruction, planned by the orchestrator.</summary>
    public Scenario Command(string text) => Capture(CaptureMode.Command, text);

    private Scenario Capture(CaptureMode mode, string text)
    {
        Begin($"{mode.Label()} \"{text}\"");
        if (mode == CaptureMode.Note) C.PressNoteKey(); else C.PressCommandKey();
        if (Snap.State is not (RelayState.NoteCapture or RelayState.CommandCapture))
        {
            Log($"  rejected: {Snap.Notice}");
            return End();
        }
        C.TextChanged(text);
        if (mode == CaptureMode.Note) C.PressNoteKey(); else C.PressCommandKey();
        // Stabilization (no relay) is 900 ms by default; advance past it and let planning run.
        _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
        return End();
    }

    /// <summary>The ask box: a direct instruction without a chord. Valid from idle and while listening.</summary>
    public Scenario Ask(string text)
    {
        Begin($"Ask \"{text}\"");
        if (!C.SubmitDirect(text)) Log($"  rejected: {Snap.Notice}");
        return End();
    }

    // ----------------------------------------------------------------------------------------
    // Listening: the note chord with the judge on
    // ----------------------------------------------------------------------------------------

    /// <summary>Opens the stream. The judge must be on (configure <c>s.Judge.Mode</c>).</summary>
    public Scenario StartListening()
    {
        Begin("Start listening");
        _heard = "";
        C.PressNoteKey();
        if (Snap.State != RelayState.NoteCapture) Log($"  rejected: {Snap.Notice}");
        else if (Snap.Listening is null) Log("  WARNING: note capture opened as dictation, not as a stream (judge off?)");
        return End();
    }

    /// <summary>
    /// Words arrive on the surface after <paramref name="after"/> of silence (default 2 s). The text is
    /// appended to what was heard, as Flow does, and the quiet timer is allowed to cut the segment.
    /// </summary>
    public Scenario Hear(string text, TimeSpan? after = null)
    {
        Begin($"Hear \"{text}\"" + (after is { } a ? $" after {a.TotalSeconds:0.#}s" : ""));
        _h.Scheduler.Advance(after ?? TimeSpan.FromSeconds(2));
        _heard = _heard.Length == 0 ? text : _heard + " " + text;
        C.TextChanged(_heard);
        _h.Scheduler.Advance(TimeSpan.FromMilliseconds(Settings.Stream.SegmentQuietMs + 10));
        return End();
    }

    /// <summary>Hear one utterance and let the judge see it: <see cref="Hear"/> followed by <see cref="Observe"/>.</summary>
    public Scenario Listen(string text) => Hear(text).Observe();

    /// <summary>Lets one observe interval elapse so the judge sees what is new.</summary>
    public Scenario Observe()
    {
        Begin("Observe (judge pass)");
        _h.Scheduler.Advance(TimeSpan.FromMilliseconds(Settings.Stream.ObserveIntervalMs + 10));
        return End();
    }

    /// <summary>Silence: nothing new arrives for this long (the buffer keeps expiring and the judge keeps checking).</summary>
    public Scenario Silence(TimeSpan duration)
    {
        Begin($"Silence {duration.TotalSeconds:0}s");
        _h.Scheduler.Advance(duration);
        return End();
    }

    /// <summary>Closes the stream: a last judge pass over what is pending, then the receipt.</summary>
    public Scenario StopListening()
    {
        Begin("Stop listening");
        C.PressNoteKey();
        _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
        _heard = "";
        return End();
    }

    // ----------------------------------------------------------------------------------------
    // Responding to tasks
    // ----------------------------------------------------------------------------------------

    public Scenario Approve(string? action = null)
    {
        var p = Pending(action);
        Begin($"Approve {p.Action} {p.ProposalId[^8..]}");
        C.Approve(p.ProposalId);
        return End();
    }

    public Scenario ApproveAll(string? taskId = null)
    {
        Begin("Approve all");
        C.ApproveAll(taskId);
        return End();
    }

    public Scenario Reject(string? action = null, string? reason = null)
    {
        var p = Pending(action);
        Begin($"Reject {p.Action} {p.ProposalId[^8..]}");
        C.Reject(p.ProposalId, reason);
        return End();
    }

    public Scenario Edit(string action, params (string Key, string Value)[] changes)
    {
        var p = Pending(action);
        Begin($"Edit {p.Action} {p.ProposalId[^8..]}: {string.Join(", ", changes.Select(c => $"{c.Key}={c.Value}"))}");
        var target = new Dictionary<string, string>(p.Target, StringComparer.Ordinal);
        foreach (var (k, v) in changes) target[k] = v;
        C.EditProposal(p.ProposalId, target);
        return End();
    }

    /// <summary>The user answers a presented task in words (a follow-up), which becomes a dialogue task.</summary>
    public Scenario Respond(string text, string? taskId = null)
    {
        var task = taskId is not null
            ? Snap.Tasks.FirstOrDefault(t => t.TaskId == taskId) ?? throw Fail($"No task {taskId}")
            : Snap.Tasks.Where(t => t.Presentation != Presentation.None).OrderByDescending(t => t.StartedAt).FirstOrDefault() ?? Response;
        Begin($"Respond to {task.TaskId[^8..]}: \"{text}\"");
        C.RecordUserResponse(task.TaskId, text);
        return End();
    }

    public Scenario DismissAttention(string? title = null)
    {
        var item = Snap.Attention.FirstOrDefault(a => title is null || a.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) ?? throw Fail($"No attention item{(title is null ? "" : " titled like " + title)}");
        Begin($"Dismiss attention \"{item.Title}\"");
        C.DismissAttention(item.ItemId);
        return End();
    }

    public Scenario Cancel()
    {
        Begin("Cancel");
        C.Cancel();
        return End();
    }

    public Scenario CancelTask(string? taskId = null)
    {
        var id = taskId ?? Snap.LiveTasks.FirstOrDefault()?.TaskId ?? throw Fail("No live task to cancel");
        Begin($"Cancel task {id[^8..]}");
        C.CancelTask(id);
        return End();
    }

    public Scenario Dismiss()
    {
        Begin("Dismiss");
        C.Dismiss();
        return End();
    }

    public Scenario Advance(TimeSpan by)
    {
        Begin($"Advance {by}");
        _h.Scheduler.Advance(by);
        return End();
    }

    public Scenario Do(string title, Action<SessionCoordinator> action)
    {
        Begin(title);
        action(C);
        return End();
    }

    /// <summary>Clean exit and a fresh process on the same data root: cross-session continuity.</summary>
    public Scenario Restart()
    {
        var clock = _h.Clock;
        _h.CleanExit();
        Log("== Clean exit ==");
        clock.Advance(TimeSpan.FromSeconds(5));
        _h = Open(null, clock);
        _activityFrom = 0;
        Log($"== Session restarted ({_h.Coordinator.SessionId[^8..]}) state {_h.Snap.State.Label()} ==");
        return this;
    }

    /// <summary>Simulated crash (no shutdown record) followed by a fresh process.</summary>
    public Scenario CrashAndRestart()
    {
        var clock = _h.Clock;
        _h.Crash();
        Log("== CRASH ==");
        clock.Advance(TimeSpan.FromSeconds(5));
        _h = Open(null, clock);
        _activityFrom = 0;
        Log($"== Session restarted after crash ({_h.Coordinator.SessionId[^8..]}) state {_h.Snap.State.Label()} ==");
        return this;
    }

    // ----------------------------------------------------------------------------------------
    // Expectations (each returns the scenario so they chain; failures include the transcript)
    // ----------------------------------------------------------------------------------------

    public Scenario ExpectState(RelayState state)
    {
        if (Snap.State != state) throw Fail($"Expected state {state.Label()} but was {Snap.State.Label()} (notice: {Snap.Notice})");
        return this;
    }

    public Scenario ExpectAnswerContains(string fragment)
    {
        var answer = Snap.Response?.Answer ?? "";
        if (!answer.Contains(fragment, StringComparison.OrdinalIgnoreCase)) throw Fail($"Expected answer to contain \"{fragment}\" but was:\n{answer}");
        return this;
    }

    public Scenario ExpectProposal(string action, string status)
    {
        var p = Snap.Response?.Proposals.FirstOrDefault(p => p.Action == action && p.Status == status);
        if (p is null) throw Fail($"Expected a {action} proposal with status {status}; had: {string.Join(", ", Snap.Response?.Proposals.Select(p => $"{p.Action}:{p.Status}") ?? [])}");
        return this;
    }

    /// <summary>A proposal with the given action and status on any task (background tasks included).</summary>
    public Scenario ExpectAnyProposal(string action, string status)
    {
        var all = Snap.Tasks.SelectMany(t => t.Proposals).ToList();
        if (!all.Any(p => p.Action == action && p.Status == status)) throw Fail($"Expected a {action} proposal with status {status} on some task; had: {string.Join(", ", all.Select(p => $"{p.Action}:{p.Status}"))}");
        return this;
    }

    public Scenario ExpectNoProposals()
    {
        if (Snap.Response?.Proposals.Count > 0) throw Fail("Expected no proposals");
        return this;
    }

    public Scenario ExpectEvent(string type, int atLeast = 1)
    {
        var n = _h.Count(type);
        if (n < atLeast) throw Fail($"Expected at least {atLeast} '{type}' record(s), found {n}");
        return this;
    }

    public Scenario ExpectNoEvent(string type)
    {
        if (_h.Count(type) > 0) throw Fail($"Expected no '{type}' records");
        return this;
    }

    public Scenario ExpectProject(string slug, bool exists = true)
    {
        var p = _h.Registry.FindActive(slug);
        if (exists && p is null) throw Fail($"Expected active project '{slug}'");
        if (!exists && p is not null) throw Fail($"Expected no active project '{slug}'");
        if (exists && !Directory.Exists(p!.RootPath)) throw Fail($"Project folder missing: {p.RootPath}");
        return this;
    }

    public Scenario ExpectOutcome(string outcome)
    {
        if (Snap.Response?.Outcome != outcome) throw Fail($"Expected outcome {outcome} but was {Snap.Response?.Outcome}");
        return this;
    }

    public Scenario ExpectReview(ReviewItemKind kind)
    {
        if (!Snap.Review.Any(r => r.Kind == kind)) throw Fail($"Expected a Review item of kind {kind}; had {string.Join(", ", Snap.Review.Select(r => r.Kind))}");
        return this;
    }

    /// <summary>Some task of this kind (and optionally status) exists in the session.</summary>
    public Scenario ExpectTask(TaskKind kind, TaskStatus? status = null, TaskOrigin? origin = null)
    {
        if (FindTask(kind, status, origin) is null)
            throw Fail($"Expected a {origin?.ToString() ?? "any-origin"} {kind} task{(status is null ? "" : " in status " + status)}; had: {DescribeTasks()}");
        return this;
    }

    public Scenario ExpectNoTask(TaskKind kind)
    {
        if (Snap.Tasks.Any(t => t.Kind == kind)) throw Fail($"Expected no {kind} task; had: {DescribeTasks()}");
        return this;
    }

    public Scenario ExpectTaskCount(int count)
    {
        if (Snap.Tasks.Count != count) throw Fail($"Expected {count} task(s); had {Snap.Tasks.Count}: {DescribeTasks()}");
        return this;
    }

    public Scenario ExpectAttention(Presentation level, string? titleFragment = null)
    {
        var hit = Snap.Attention.FirstOrDefault(a => a.Level == level && (titleFragment is null || a.Title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase) || a.Detail.Contains(titleFragment, StringComparison.OrdinalIgnoreCase)));
        if (hit is null) throw Fail($"Expected a {level} attention item{(titleFragment is null ? "" : " mentioning \"" + titleFragment + "\"")}; had: {DescribeAttention()}");
        return this;
    }

    public Scenario ExpectNoAttention(Presentation? level = null)
    {
        if (Snap.Attention.Any(a => level is null || a.Level == level)) throw Fail($"Expected no {level?.ToString() ?? ""} attention items; had: {DescribeAttention()}");
        return this;
    }

    public Scenario ExpectListening(bool listening = true)
    {
        if ((Snap.Listening is not null) != listening) throw Fail(listening ? "Expected to be listening" : "Expected not to be listening");
        return this;
    }

    public Scenario ExpectExcerpts(int count)
    {
        var n = _h.Excerpts.All().Count;
        if (n != count) throw Fail($"Expected {count} excerpt(s) on disk, found {n}");
        return this;
    }

    public Scenario ExpectPreference(string key, string value)
    {
        var actual = _h.Preferences.Get(key);
        if (!string.Equals(actual, value, StringComparison.Ordinal)) throw Fail($"Expected preference {key}={value} but was {actual ?? "(unset)"}");
        return this;
    }

    public string Transcript() => _transcript.ToString();

    public Exception Fail(string message) => new Xunit.Sdk.XunitException(message + "\n\n--- transcript ---\n" + Transcript());

    public TaskView? FindTask(TaskKind kind, TaskStatus? status = null, TaskOrigin? origin = null)
        => Snap.Tasks.FirstOrDefault(t => t.Kind == kind && (status is null || t.Status == status) && (origin is null || t.Origin == origin));

    private ProposalView Pending(string? action)
        => Snap.PendingProposals.FirstOrDefault(p => action is null || p.Action == action) ?? throw Fail($"No pending proposal{(action is null ? "" : " for " + action)}");

    private string DescribeTasks() => string.Join(", ", Snap.Tasks.Select(t => $"{t.Origin}/{t.Kind}:{t.Status}"));
    private string DescribeAttention() => string.Join(", ", Snap.Attention.Select(a => $"{a.Level} \"{a.Title}\""));

    // ----------------------------------------------------------------------------------------
    // Transcript rendering
    // ----------------------------------------------------------------------------------------

    private void Begin(string title)
    {
        _step++;
        _activityFrom = _h.Snap.Activity.Count > 0 ? _h.Snap.Activity[^1].Seq : 0;
        Log($"\n== Step {_step}: {title} ==");
    }

    private Scenario End()
    {
        // Without inline posting, synchronous planners still hand their result back through the scheduler; drain what is already there.
        if (!_inlinePost) _h.Scheduler.PumpUntil(() => true, TimeSpan.Zero);
        var s = Snap;
        Log($"  state: {s.State.Label()}" + (s.Notice is null ? "" : $"   notice: {s.Notice}") + (s.Receipt is null ? "" : $"   receipt: {s.Receipt}"));
        if (s.Listening is { } l)
            Log($"  listening: {l.HeldSegments} held ({l.HeldSeconds:0}s of {l.WindowSeconds:0}s) · {l.TotalSegments} heard · {l.JudgePasses} pass(es) · {l.Findings} finding(s) · {l.Excerpts} excerpt(s) · {l.Tasks} task(s)" + (l.Judging ? " · judging" : "") + (l.LastError is null ? "" : $" · error: {l.LastError}"));
        foreach (var t in s.Tasks.Where(t => t.Live || t.CompletedAt is null || t.CompletedAt >= _h.Clock.UtcNow - TimeSpan.FromSeconds(60)).OrderBy(t => t.StartedAt))
        {
            if (!t.Live && !s.Activity.Any(a => a.Seq > _activityFrom && a.Text.Contains(t.TaskId[^8..], StringComparison.Ordinal)) && s.Response?.TaskId != t.TaskId) continue;
            RenderTask(t, s.Response?.TaskId == t.TaskId);
        }
        foreach (var a in s.Attention)
        {
            var mark = _seenAttention.Add(a.ItemId) ? "+" : " ";
            Log($"  {mark} attention [{a.Level}] {a.Title} — {a.Detail}" + (a.Occurrences > 1 ? $" (x{a.Occurrences})" : "") + (a.Pinned ? " (pinned)" : ""));
        }
        foreach (var item in s.Review) Log($"  review [{item.Kind}] {item.Title}");
        foreach (var a in s.Activity.Where(a => a.Seq > _activityFrom)) Log($"  {a.Timestamp:HH:mm:ss} {a.Type,-28} {a.Text}");
        return this;
    }

    private void RenderTask(TaskView r, bool isResponse)
    {
        var head = isResponse ? "task*" : "task ";
        Log($"  {head} {r.TaskId[^8..]} [{r.Origin}/{r.Kind}/{r.Lane}] {r.Status} ({r.Producer}{(r.Live ? ", live" : "")}{(r.Foreground ? ", fg" : "")}): {r.Summary}" + (r.Outcome is null ? "" : $" → {r.Outcome}"));
        if (r.Origin != TaskOrigin.Direct) Log($"    why: {r.Why} (confidence {r.Confidence:0.00}{(r.ExcerptId is null ? "" : ", excerpt " + r.ExcerptId[^8..])})");
        foreach (var step in r.Steps) Log($"    - {step}");
        foreach (var call in r.ToolCalls) Log($"    tool {call.Tool}({string.Join(", ", call.Args.Select(a => $"{a.Key}={a.Value}"))}) → {(call.Ok ? "ok" : "FAILED")} {call.Summary}");
        foreach (var m in r.ModelCalls) Log($"    model {m.Model}@{m.Host} {(m.Ok ? "ok" : "FAILED " + m.Error)} {m.PromptTokens}+{m.CompletionTokens} tok {m.ElapsedMs} ms");
        if (r.Knowledge.Missing.Count > 0 || r.Knowledge.CapabilityGap) Log($"    knowledge: missing [{string.Join("; ", r.Knowledge.Missing)}]{(r.Knowledge.CapabilityGap ? " capability gap" : "")}");
        if (r.Consistent is false) Log("    consistency: CONFLICT with stored notes");
        if (r.Answer is not null) Log("    answer: " + r.Answer.Replace("\n", "\n            "));
        foreach (var c in r.Citations) Log($"    cite: [{c.Kind} {c.Id[^8..]}{(c.ProjectSlug is null ? "" : " " + c.ProjectSlug)}] {c.Excerpt}" + (c.Span is null ? "" : $" @ {c.Span.EventId[^8..]}:{c.Span.Start}-{c.Span.End}"));
        foreach (var p in r.Proposals)
        {
            Log($"    proposal [{p.Status}] {p.Action} ({p.Tier}) {p.Title}" + (p.DependsOn.Count > 0 ? $" after {string.Join(",", p.DependsOn.Select(d => d[^8..]))}" : "") + (p.GrantedBy is null ? "" : $" granted by {p.GrantedBy}"));
            foreach (var reason in p.Reasons) Log($"        · {reason}");
            if (p.ResultSummary is not null) Log($"        → {p.ResultSummary}");
            if (p.Error is not null) Log($"        ✗ {p.Error}");
        }
        if (r.Presentation != Presentation.None || r.PresentationReason is not null) Log($"    presented: {r.Presentation} ({r.PresentationReason})");
        if (r.UserResponse is not null) Log($"    user said: {r.UserResponse}");
        if (r.Cost.ModelCalls > 0 || r.Cost.ToolCalls > 0) Log($"    cost: {r.Cost.ModelCalls} model call(s) {r.Cost.TotalTokens} tok · {r.Cost.ToolCalls} tool call(s) · {r.Cost.WallMs} ms");
    }

    private void Log(string line) => _transcript.AppendLine(line);

    public void Dispose()
    {
        _h.Dispose();
        try { if (WorkspacePath.Length > 0 && Directory.Exists(WorkspacePath)) Directory.Delete(WorkspacePath, recursive: true); } catch { }
    }
}

/// <summary>Returns scripted plans by instruction; unknown instructions are "not understood".</summary>
public sealed class CannedOrchestrator : IOrchestrator
{
    private readonly Dictionary<string, Func<TurnRequest, TurnContext, TurnPlan>> _plans = new(StringComparer.OrdinalIgnoreCase);
    private Func<TurnRequest, TurnContext, TurnPlan>? _fallback;

    public string Name => "canned";
    public List<TurnRequest> Requests { get; } = new();
    public List<TurnContext> Contexts { get; } = new();

    public CannedOrchestrator On(string instruction, Func<TurnRequest, TurnContext, TurnPlan> plan) { _plans[instruction] = plan; return this; }
    public CannedOrchestrator On(string instruction, TurnPlan plan) => On(instruction, (_, _) => plan);
    /// <summary>Plans anything not matched by instruction (observed tasks carry focused prompts the test may not want to spell out).</summary>
    public CannedOrchestrator Otherwise(Func<TurnRequest, TurnContext, TurnPlan> plan) { _fallback = plan; return this; }

    public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Contexts.Add(context);
        if (_plans.TryGetValue(request.Instruction.Trim(), out var plan)) return Task.FromResult(plan(request, context));
        if (_fallback is not null) return Task.FromResult(_fallback(request, context));
        return Task.FromResult(TurnPlan.NotUnderstood(Name, "no canned plan"));
    }
}

/// <summary>Holds every plan until the test releases it, so cancellation and timeout during PLANNING are deterministic.</summary>
public sealed class PausingOrchestrator : IOrchestrator
{
    private readonly IOrchestrator _inner;
    private TaskCompletionSource<TurnPlan>? _gate;
    private (TurnRequest Request, TurnContext Context)? _pending;

    public PausingOrchestrator(IOrchestrator inner) => _inner = inner;

    public string Name => "pausing-" + _inner.Name;
    public bool IsPaused => _gate is not null;
    public CancellationToken LastToken { get; private set; }

    public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
    {
        LastToken = cancellationToken;
        // Continuations run synchronously on the completing (test) thread so the coordinator is never touched from elsewhere.
        _gate = new TaskCompletionSource<TurnPlan>();
        _pending = (request, context);
        cancellationToken.Register(() => _gate.TrySetCanceled(cancellationToken));
        return _gate.Task;
    }

    /// <summary>Lets the inner orchestrator produce the plan now. Returns false when nothing was waiting.</summary>
    public bool Release()
    {
        if (_gate is null || _pending is null) return false;
        var (request, context) = _pending.Value;
        var gate = _gate;
        _gate = null;
        _pending = null;
        var plan = _inner.PlanAsync(request, context, CancellationToken.None).GetAwaiter().GetResult();
        return gate.TrySetResult(plan);
    }

    public bool Fail(Exception ex)
    {
        var gate = _gate;
        _gate = null;
        _pending = null;
        return gate?.TrySetException(ex) ?? false;
    }
}

/// <summary>An orchestrator that throws, for failure-path tests.</summary>
public sealed class ThrowingOrchestrator : IOrchestrator
{
    public string Name => "throwing";
    public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("model exploded");
}

/// <summary>
/// A judge driven by the test: findings are keyed by a phrase; a pass returns the findings of every
/// phrase that appears in a new segment, anchored to that segment. Everything else is "nothing".
/// Stands in for RELAY0 so listening scenarios are deterministic and say exactly what was heard.
/// </summary>
public sealed class ScriptedJudge : IJudge
{
    private readonly List<(string Phrase, Func<StreamSegment, IReadOnlyList<StreamSegment>, JudgeFinding> Finding)> _rules = new();
    private Func<JudgeRequest, JudgeDecision>? _direct;

    public string Name => "scripted";
    public List<JudgeRequest> Requests { get; } = new();
    public int PromptTokens { get; init; } = 120;
    public int CompletionTokens { get; init; } = 40;
    public Exception? Throws { get; set; }
    public TimeSpan? Delay { get; set; }

    /// <summary>When a new segment contains the phrase, the finding is produced for it.</summary>
    public ScriptedJudge When(string phrase, Func<StreamSegment, JudgeFinding> finding) { _rules.Add((phrase, (seg, _) => finding(seg))); return this; }

    /// <summary>Like <see cref="When(string, Func{StreamSegment, JudgeFinding})"/> but the rule also sees the whole window, so a finding can name several segments.</summary>
    public ScriptedJudge When(string phrase, Func<StreamSegment, IReadOnlyList<StreamSegment>, JudgeFinding> finding) { _rules.Add((phrase, finding)); return this; }

    public ScriptedJudge When(string phrase, TaskKind kind, string focusedPrompt, double confidence = 0.9, string? topic = null, string? projectHint = null, string? noteText = null, Presentation? presentation = null, string? mergeKey = null)
        => When(phrase, seg => new JudgeFinding(kind, confidence, $"{kind}: {phrase}", $"heard \"{phrase}\"", focusedPrompt, [seg.SegmentId], topic, projectHint, presentation, noteText, noteText is null ? null : "note", mergeKey));

    public ScriptedJudge OnDirect(Func<JudgeRequest, JudgeDecision> direct) { _direct = direct; return this; }

    public Task<JudgeDecision> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Throws is not null) throw Throws;
        if (request.Origin == TaskOrigin.Direct && _direct is not null) return Task.FromResult(_direct(request));
        var findings = new List<JudgeFinding>();
        foreach (var segment in request.Window.Where(s => request.NewSegmentIds.Contains(s.SegmentId)))
            foreach (var (phrase, finding) in _rules)
                if (segment.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase)) findings.Add(finding(segment, request.Window));
        var decision = new JudgeDecision(findings, Name, PromptTokens, CompletionTokens, (long)(Delay?.TotalMilliseconds ?? 30));
        return Task.FromResult(decision);
    }
}

/// <summary>A judge that never answers: it honours cancellation and nothing else, for the timeout path.</summary>
public sealed class HangingJudge : IJudge
{
    public string Name => "model:hanging";
    public int Calls { get; private set; }

    public Task<JudgeDecision> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        // Continuations run synchronously on the cancelling (test) thread so the coordinator is never touched from elsewhere.
        var tcs = new TaskCompletionSource<JudgeDecision>();
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }
}