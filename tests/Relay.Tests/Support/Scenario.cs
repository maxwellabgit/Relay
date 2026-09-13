using System.Text;
using Relay.Core.Agents;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ledger;
using Relay.Core.Mind;
using Relay.Core.Model;
using Relay.Core.Orchestration;
using Relay.Core.Search;
using Relay.Core.Session;
using Relay.Core.State;
using Relay.Core.Storage;
using Relay.Core.Tasks;
using TaskStatus = Relay.Core.Tasks.TaskStatus;

namespace Relay.Tests.Support;

/// <summary>
/// Prompt-driven scenarios against the real coordinator, stores, mind, policy engine and executor.
/// Each step drives the same public surface the desktop UI uses (chords, surface text, the ask box,
/// approve, reject, edit, cancel, respond) and records a transcript that shows exactly how RELAY0
/// handled it: state, tasks, plans, tool calls, proposals, decisions, executions, attention, ledger lines.
/// </summary>
public sealed class Scenario : IDisposable
{
    private readonly TempRoot _tmp;
    private readonly IWorkerHost? _workerHost;
    private readonly Func<ExternalModelProfile, IModelClient>? _externalClients;
    private readonly Relay.Core.Mind.IMind? _mind;
    private readonly IModelClient? _toolDrafter;
    private readonly IModelClient? _digester;
    private readonly ISearchClient? _searchClient;
    private readonly bool _inlinePost;
    private readonly MemorySecretStore _secrets = new();
    private readonly StringBuilder _transcript = new();
    private readonly HashSet<string> _seenAttention = new(StringComparer.Ordinal);
    private Harness _h;
    private int _step;
    private long _activityFrom;
    private string _heard = "";

    private Scenario(TempRoot tmp, Action<RelaySettings>? configure, IWorkerHost? workerHost, FixedClock? clock, Func<ExternalModelProfile, IModelClient>? externalClients, bool inlinePost, Relay.Core.Mind.IMind? mind, IModelClient? toolDrafter, IModelClient? digester, ISearchClient? searchClient)
    {
        _tmp = tmp;
        _workerHost = workerHost;
        _externalClients = externalClients;
        _mind = mind;
        _toolDrafter = toolDrafter;
        _digester = digester;
        _searchClient = searchClient;
        _inlinePost = inlinePost;
        _h = Open(configure, clock);
        Log($"== Session started ({_h.Coordinator.SessionId[^8..]}) state {_h.Snap.State.Label()} mind {(_h.Snap.MindReady ? "ready" : "none")} ==");
    }

    /// <param name="inlinePost">False when background threads (external requests, real workers) post completions; the test then drains them with <see cref="PumpUntil"/>.</param>
    /// <param name="mind">Relay's mind: it runs every task and reads every conversation when listening is on. Without one no task can run.</param>
    /// <param name="toolDrafter">The model that drafts tools for the mind's build move (slice 6); needs <paramref name="workerHost"/> for the sandbox.</param>
    /// <param name="digester">The local model that digests delegate replies into feed lines (slice 5); null means the reply's own first lines.</param>
    /// <param name="searchClient">Online search provider; when null and search is enabled in settings, Harness supplies a fake so allowSearch can proceed.</param>
    public static Scenario New(TempRoot tmp, Action<RelaySettings>? configure = null, IWorkerHost? workerHost = null, FixedClock? clock = null,
        Func<ExternalModelProfile, IModelClient>? externalClients = null, bool inlinePost = true, Relay.Core.Mind.IMind? mind = null, IModelClient? toolDrafter = null, IModelClient? digester = null, ISearchClient? searchClient = null)
        => new(tmp, configure, workerHost, clock, externalClients, inlinePost, mind, toolDrafter, digester, searchClient);

    private Harness Open(Action<RelaySettings>? configure, FixedClock? clock)
        => new Harness(_tmp.Root, configure: configure, workerHost: _workerHost, clock: clock, externalClients: _externalClients, secrets: _secrets, inlinePost: _inlinePost, mind: _mind, toolDrafter: _toolDrafter, digester: _digester, searchClient: _searchClient).Start();

    /// <summary>Drains work posted by background threads on this thread until the condition holds; fails the scenario on timeout.</summary>
    public Scenario PumpUntil(string what, Func<bool> condition, TimeSpan? timeout = null)
    {
        Begin($"Pump until {what}");
        if (!_h.Scheduler.PumpUntil(condition, timeout ?? TimeSpan.FromSeconds(10))) throw Fail($"Timed out waiting for {what}");
        return End();
    }

    /// <summary>The foreground task has finished (or there is none). Session Ready is not enough after the state collapse — the session stays Ready while a task runs.</summary>
    public bool ForegroundSettled => Snap.Response is null or { Live: false };

    /// <summary>A proposal is waiting for the user, or the foreground task has already settled.</summary>
    public bool AwaitingUserOrSettled => Snap.PendingProposals.Any() || Snap.Response?.Status == TaskStatus.AwaitingApproval || ForegroundSettled;

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
    /// Turns listening on through the settings path the UI uses, so the note chord opens a conversation from here on.
    /// Reading it needs a mind: build the scenario with one.
    /// </summary>
    public Scenario WithListening()
    {
        var problems = C.UpdateSettings(x => x.Listening.Enabled = true);
        if (problems.Count > 0) throw Fail("Listening could not be enabled: " + string.Join(" ", problems));
        Log("-- listening on: the note chord now opens a conversation" + (Snap.MindReady ? "" : " (but there is no mind to read it)"));
        return this;
    }

    // ----------------------------------------------------------------------------------------
    // Direct input: the chords and the ask box
    // ----------------------------------------------------------------------------------------

    /// <summary>Note chord with listening off: a dictated note, organized when it settles.</summary>
    public Scenario Note(string text) => Capture(CaptureMode.Note, text);
    /// <summary>Command chord: a direct instruction, run by the mind.</summary>
    public Scenario Command(string text) => Capture(CaptureMode.Command, text);

    // ----------------------------------------------------------------------------------------
    // Building a world
    // ----------------------------------------------------------------------------------------
    //
    // A test's world is built through the same calls the Projects and Memory panels make, not by asking
    // the mind to interpret a sentence. That keeps the setup of a test out of what the test is about:
    // the mind is then scripted only for the moves the test is actually watching.

    /// <summary>Creates a project the way the Projects panel does, and checks it exists.</summary>
    public Scenario Project(string name, string? slug = null)
        => Do($"create project {name}", c =>
        {
            if (!c.CreateProject(name, slug)) throw Fail($"Could not create project '{name}': {Snap.Notice}");
        }).ExpectProject(slug ?? Slug(name));

    /// <summary>Files every note still waiting in staging into this project, the way the Memory panel does.</summary>
    public Scenario FileAll(string slug)
        => Do($"file all notes under {slug}", c =>
        {
            var project = _h.Registry.FindActive(slug) ?? throw Fail($"No active project '{slug}' to file into");
            var staged = Snap.Inbox.Select(n => n.NoteId).ToList();
            if (staged.Count == 0) throw Fail($"Nothing is staged to file under '{slug}'");
            foreach (var noteId in staged)
                if (!c.RouteDraftNote(noteId, project.Id)) throw Fail($"Could not file note {noteId[^8..]} under '{slug}': {Snap.Notice}");
        });

    /// <summary>The last note dictated, filed into this project with an optional note type.</summary>
    public Scenario FileLast(string slug, string? type = null)
        => Do($"file the last note under {slug}" + (type is null ? "" : $" as a {type}"), c =>
        {
            var project = _h.Registry.FindActive(slug) ?? throw Fail($"No active project '{slug}' to file into");
            var note = Snap.Inbox.LastOrDefault() ?? throw Fail($"No staged note to file under '{slug}'");
            if (!c.PromoteDraftNote(note.NoteId, project.Id, type)) throw Fail($"Could not file note {note.NoteId[^8..]} under '{slug}': {Snap.Notice}");
        });

    private static string Slug(string name) => new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');

    private Scenario Capture(CaptureMode mode, string text)
    {
        Begin($"{mode.Label()} \"{text}\"");
        if (mode == CaptureMode.Note) C.PressNoteKey(); else C.PressCommandKey();
        if (Snap.Capture != CapturePhase.Capturing)
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
    // Listening: the note chord opening a conversation the mind reads
    // ----------------------------------------------------------------------------------------

    /// <summary>Opens the stream. Needs listening on (<c>s.Listening.Enabled</c>) and a mind.</summary>
    public Scenario StartListening()
    {
        Begin("Start listening");
        _heard = "";
        C.PressNoteKey();
        if (Snap.Listening is null) Log($"  rejected: {Snap.Notice ?? "note capture opened as dictation, not as a stream (listening off, or no mind?)"}");
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

    /// <summary>Hear one utterance and let the mind read it: <see cref="Hear"/> followed by <see cref="Observe"/>.</summary>
    public Scenario Listen(string text) => Hear(text).Observe();

    /// <summary>Lets one observe interval elapse so the mind reads what is new.</summary>
    public Scenario Observe()
    {
        Begin("Observe (listening pass)");
        _h.Scheduler.Advance(TimeSpan.FromMilliseconds(Settings.Stream.ObserveIntervalMs + 10));
        return End();
    }

    /// <summary>Silence: nothing new arrives for this long (the buffer keeps expiring and the mind keeps reading).</summary>
    public Scenario Silence(TimeSpan duration)
    {
        Begin($"Silence {duration.TotalSeconds:0}s");
        _h.Scheduler.Advance(duration);
        return End();
    }

    /// <summary>Closes the stream: one last pass over what is unread, then the receipt.</summary>
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
            Log($"  listening: {l.HeldSegments} held ({l.HeldSeconds:0}s of {l.WindowSeconds:0}s) · {l.TotalSegments} heard · {l.Passes} pass(es) · {l.Raised} raised · {l.Excerpts} excerpt(s) · {l.Tasks} task(s)" + (l.Reading ? " · reading" : "") + (l.LastError is null ? "" : $" · error: {l.LastError}"));
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

/// <summary>
/// Holds every step until the test releases it, so cancellation and a timeout mid-step are deterministic.
/// The inner mind decides what the released step is; without one the step is a plain answer.
/// </summary>
public sealed class PausingMind : Relay.Core.Mind.IMind
{
    private readonly Relay.Core.Mind.IMind? _inner;
    private TaskCompletionSource<MindStep>? _gate;
    private MindRequest? _pending;

    public PausingMind(Relay.Core.Mind.IMind? inner = null) => _inner = inner;

    public string Name => "mind:pausing" + (_inner is null ? "" : "-" + _inner.Name);
    public bool IsPaused => _gate is not null;
    public CancellationToken LastToken { get; private set; }

    public Task<MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken)
    {
        LastToken = cancellationToken;
        // Continuations run synchronously on the completing (test) thread so the coordinator is never touched from elsewhere.
        _gate = new TaskCompletionSource<MindStep>();
        _pending = request;
        cancellationToken.Register(() => _gate.TrySetCanceled(cancellationToken));
        return _gate.Task;
    }

    /// <summary>Lets the step through now. Returns false when nothing was waiting.</summary>
    public bool Release()
    {
        if (_gate is null || _pending is null) return false;
        var request = _pending;
        var gate = _gate;
        _gate = null;
        _pending = null;
        var step = _inner is null
            ? MindStep.Of(ScriptedMind.Say("Done."), "Answered.")
            : _inner.StepAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        return gate.TrySetResult(step);
    }

    public bool Fail(Exception ex)
    {
        var gate = _gate;
        _gate = null;
        _pending = null;
        return gate?.TrySetException(ex) ?? false;
    }
}

/// <summary>A mind that throws rather than answering, for failure-path tests.</summary>
public sealed class ThrowingMind : Relay.Core.Mind.IMind
{
    public string Name => "mind:throwing";
    public Task<MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("model exploded");
}

/// <summary>
/// A mind that reads conversations by phrase: a pass raises the work paired with every phrase that appears in a
/// line it has not raised on yet, then waits. Stands in for RELAY0 so listening scenarios are deterministic and
/// say exactly what was heard. The tasks its raises start are run by <see cref="Working"/>.
/// </summary>
public sealed class ListeningMind : Relay.Core.Mind.IMind
{
    private readonly List<(string Phrase, Func<WindowLine, RaiseMove> Raise)> _rules = new();
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);

    public string Name => "mind:scripted";
    public List<MindRequest> Requests { get; } = new();
    /// <summary>Only the listening passes, which is what these scenarios are about.</summary>
    public List<MindRequest> Passes => Requests.Where(r => r.Observing).ToList();
    public Exception? Throws { get; set; }
    /// <summary>The significance every read reports. Below the decider's bar a raise is refused.</summary>
    public double Significance { get; set; } = 0.8;
    /// <summary>What runs the tasks the raises start. Without one a raised task waits, which is all a listening scenario needs.</summary>
    public ScriptedMind? Working { get; set; }

    /// <summary>Sets <see cref="Working"/> and returns this mind, so a scenario reads as one expression.</summary>
    public ListeningMind Works(ScriptedMind working) { Working = working; return this; }

    /// <summary>When a line contains the phrase, the raise is built from it.</summary>
    public ListeningMind When(string phrase, Func<WindowLine, RaiseMove> raise) { _rules.Add((phrase, raise)); return this; }

    public ListeningMind When(string phrase, string kind, string objective, string? note = null, string? noteType = null, string? project = null, string? topic = null, string? mergeKey = null)
        => When(phrase, line => new RaiseMove(kind, objective, [line.Label], $"heard \"{phrase}\"", note, noteType, project, topic, mergeKey));

    /// <summary>Like <see cref="When(string, Func{WindowLine, RaiseMove})"/> but the raise also names every line held, so an excerpt can span the window.</summary>
    public ListeningMind WhenWholeWindow(string phrase, string kind, string objective, string? project = null)
        => When(phrase, line => new RaiseMove(kind, objective, [WholeWindow], $"heard \"{phrase}\"", Project: project));

    /// <summary>A stand-in label the mind expands to every label the pass showed.</summary>
    private const string WholeWindow = "*";

    public Task<Relay.Core.Mind.MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Throws is not null) throw Throws;
        if (!request.Observing)
            return Working is null
                ? Task.FromResult(MindStep.Of(ScriptedMind.Wait("this task is not mine to run"), "Not mine."))
                : Working.StepAsync(request, cancellationToken);

        var window = request.Transcript.OfType<WindowObserved>().LastOrDefault();
        var lines = window?.Lines.ToList() ?? [];
        foreach (var line in lines)
            foreach (var (phrase, raise) in _rules)
            {
                if (!line.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_done.Add(line.SegmentId + "|" + phrase)) continue;
                var move = raise(line);
                if (move.Segments is [WholeWindow]) move = move with { Segments = lines.Select(l => l.Label).ToList() };
                return Task.FromResult(MindStep.Of(move, $"Raising what was said in {line.Label}.", Read()));
            }
        return Task.FromResult(MindStep.Of(ScriptedMind.Wait("nothing that needs Relay"), "Listening on.", Read()));
    }

    private MindRead Read() => new("a conversation", 0.3, [MindRead.NeedNone], Significance, 0, RiskRead.None);
}

/// <summary>A mind that never answers: it honours cancellation and nothing else, for the pass-timeout path.</summary>
public sealed class HangingMind : Relay.Core.Mind.IMind
{
    public string Name => "model:hanging";
    public int Calls { get; private set; }

    public Task<Relay.Core.Mind.MindStep> StepAsync(Relay.Core.Mind.MindRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        // Continuations run synchronously on the cancelling (test) thread so the coordinator is never touched from elsewhere.
        var tcs = new TaskCompletionSource<Relay.Core.Mind.MindStep>();
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }
}