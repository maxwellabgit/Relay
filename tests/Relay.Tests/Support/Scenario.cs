using System.Text;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ledger;
using Relay.Core.Orchestration;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Storage;

namespace Relay.Tests.Support;

/// <summary>
/// Prompt-driven scenarios against the real coordinator, stores, policy engine and executor.
/// Each step drives the same public surface the desktop UI uses (hotkeys, capture text, approve,
/// reject, edit, cancel) and records a transcript that shows exactly how the orchestrator handled
/// the instruction: state, plan, tool calls, proposals, decisions, executions, and ledger lines.
/// </summary>
public sealed class Scenario : IDisposable
{
    private readonly TempRoot _tmp;
    private readonly Action<RelaySettings>? _configure;
    private readonly IOrchestrator? _orchestrator;
    private readonly IWorkerOperations? _workers;
    private readonly StringBuilder _transcript = new();
    private Harness _h;
    private int _step;
    private long _activityFrom;

    private Scenario(TempRoot tmp, Action<RelaySettings>? configure, IOrchestrator? orchestrator, IWorkerOperations? workers, FixedClock? clock)
    {
        _tmp = tmp;
        _configure = configure;
        _orchestrator = orchestrator;
        _workers = workers;
        _h = new Harness(tmp.Root, configure: configure, orchestrator: orchestrator, workers: workers, clock: clock).Start();
        Log($"== Session started ({_h.Coordinator.SessionId[^8..]}) state {_h.Snap.State.Label()} ==");
    }

    public static Scenario New(TempRoot tmp, Action<RelaySettings>? configure = null, IOrchestrator? orchestrator = null, IWorkerOperations? workers = null, FixedClock? clock = null)
        => new(tmp, configure, orchestrator, workers, clock);

    public Harness H => _h;
    public SessionCoordinator C => _h.Coordinator;
    public RelaySnapshot Snap => _h.Snap;
    public TurnResponse Response => Snap.Response ?? throw new InvalidOperationException("No turn response.");
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

    public Scenario Note(string text) => Capture(CaptureMode.Note, text);
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

    public Scenario Approve(string? action = null)
    {
        var p = Pending(action);
        Begin($"Approve {p.Action} {p.ProposalId[^8..]}");
        C.Approve(p.ProposalId);
        return End();
    }

    public Scenario ApproveAll()
    {
        Begin("Approve all");
        C.ApproveAll();
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

    public Scenario Cancel()
    {
        Begin("Cancel");
        C.Cancel();
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
        _h = new Harness(_tmp.Root, configure: null, orchestrator: _orchestrator, workers: _workers, clock: clock).Start();
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
        _h = new Harness(_tmp.Root, configure: null, orchestrator: _orchestrator, workers: _workers, clock: clock).Start();
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

    public string Transcript() => _transcript.ToString();

    public Exception Fail(string message) => new Xunit.Sdk.XunitException(message + "\n\n--- transcript ---\n" + Transcript());

    private ProposalView Pending(string? action)
        => Snap.PendingProposals.FirstOrDefault(p => action is null || p.Action == action) ?? throw Fail($"No pending proposal{(action is null ? "" : " for " + action)}");

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
        var s = Snap;
        Log($"  state: {s.State.Label()}" + (s.Notice is null ? "" : $"   notice: {s.Notice}") + (s.Receipt is null ? "" : $"   receipt: {s.Receipt}"));
        if (s.Response is { } r)
        {
            Log($"  turn {r.TurnId[^8..]} ({r.Kind}, {r.Producer}{(r.Live ? ", live" : "")}): {r.Summary}" + (r.Outcome is null ? "" : $" → {r.Outcome}"));
            foreach (var step in r.Steps) Log($"    - {step}");
            if (r.Answer is not null) Log("  answer: " + r.Answer.Replace("\n", "\n          "));
            foreach (var c in r.Citations) Log($"  cite: [{c.Kind} {c.Id[^8..]}{(c.ProjectSlug is null ? "" : " " + c.ProjectSlug)}] {c.Excerpt}" + (c.Span is null ? "" : $" @ {c.Span.EventId[^8..]}:{c.Span.Start}-{c.Span.End}"));
            foreach (var p in r.Proposals)
            {
                Log($"  proposal [{p.Status}] {p.Action} ({p.Tier}) {p.Title}");
                foreach (var reason in p.Reasons) Log($"      · {reason}");
                if (p.ResultSummary is not null) Log($"      → {p.ResultSummary}");
                if (p.Error is not null) Log($"      ✗ {p.Error}");
            }
        }
        foreach (var item in s.Review) Log($"  review [{item.Kind}] {item.Title}");
        foreach (var a in s.Activity.Where(a => a.Seq > _activityFrom)) Log($"  {a.Timestamp:HH:mm:ss} {a.Type,-28} {a.Text}");
        return this;
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

    public string Name => "canned";
    public List<TurnRequest> Requests { get; } = new();

    public CannedOrchestrator On(string instruction, Func<TurnRequest, TurnContext, TurnPlan> plan) { _plans[instruction] = plan; return this; }
    public CannedOrchestrator On(string instruction, TurnPlan plan) => On(instruction, (_, _) => plan);

    public Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_plans.TryGetValue(request.Instruction.Trim(), out var plan) ? plan(request, context) : TurnPlan.NotUnderstood(Name, "no canned plan"));
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
