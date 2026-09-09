using Relay.Core.Decisions;
using Relay.Core.Mind;

namespace Relay.Tests.Support;

/// <summary>
/// A consequence owner for loop tests: every move is recorded, and each handler can be replaced so a
/// test decides what the world answers (a tool result, a policy verdict, a streamed partial).
/// </summary>
public sealed class FakeLoopHost : ILoopHost
{
    private readonly DateTimeOffset _at = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public List<MindStep> Steps { get; } = new();
    public List<string> Calls { get; } = new();
    public List<SayMove> Says { get; } = new();
    public List<string> Waits { get; } = new();
    public LoopResult? Result { get; private set; }
    public DecisionRecord? LastFof { get; private set; }

    public Func<UseToolMove, MoveOutcome> OnTool { get; set; }
    public Func<ProposeMove, DecisionRecord?, MoveOutcome> OnPropose { get; set; }
    public Func<DelegateMove, MoveOutcome> OnDelegate { get; set; }
    public Func<BuildMove, DecisionRecord, MoveOutcome> OnBuild { get; set; }
    public Func<AskUserMove, MoveOutcome> OnAsk { get; set; }
    public Func<StopMove, string, MoveOutcome> OnStop { get; set; }

    public FakeLoopHost()
    {
        OnTool = m => MoveOutcome.Of(new ToolObserved(_at, m.Tool, m.Args, true, "0 hit(s)", "[]", []));
        OnPropose = (m, _) => MoveOutcome.Of(
            new PolicyObserved(_at, "p1", m.Action, PolicyObserved.Allowed, []),
            new ExecutionObserved(_at, "p1", m.Action, true, "done", new Dictionary<string, string>()));
        OnDelegate = m => MoveOutcome.Wait(Relay.Core.Mind.Waits.Delegate, new DelegateObserved(_at, "r1", m.Profile, DelegateObserved.Started, 0, null));
        OnBuild = (m, _) => MoveOutcome.Of(new BuildObserved(_at, m.Name, BuildObserved.Promoted, "3 tests passed"));
        OnAsk = m => MoveOutcome.Wait(Relay.Core.Mind.Waits.User);
        OnStop = (_, waited) => MoveOutcome.Of(new DelegateObserved(_at, "r1", "research", DelegateObserved.Stopped, 0, null));
    }

    public void Stepped(TaskLoop loop, MindStep step) { Steps.Add(step); Calls.Add(step.Ok ? "step:" + step.Move!.Type : "step:failed"); }
    public void Said(TaskLoop loop, SayMove move) { Says.Add(move); Calls.Add("said"); }
    public Task<MoveOutcome> UseToolAsync(TaskLoop loop, UseToolMove move, CancellationToken ct) { Calls.Add("tool:" + move.Tool); return Task.FromResult(OnTool(move)); }
    public Task<MoveOutcome> ProposeAsync(TaskLoop loop, ProposeMove move, DecisionRecord? fof, CancellationToken ct) { Calls.Add("propose:" + move.Action); LastFof = fof; return Task.FromResult(OnPropose(move, fof)); }
    public Task<MoveOutcome> DelegateAsync(TaskLoop loop, DelegateMove move, CancellationToken ct) { Calls.Add("delegate:" + move.Profile); return Task.FromResult(OnDelegate(move)); }
    public Task<MoveOutcome> BuildAsync(TaskLoop loop, BuildMove move, DecisionRecord fof, CancellationToken ct) { Calls.Add("build:" + move.Name); LastFof = fof; return Task.FromResult(OnBuild(move, fof)); }
    public Task<MoveOutcome> AskUserAsync(TaskLoop loop, AskUserMove move, CancellationToken ct) { Calls.Add("ask"); return Task.FromResult(OnAsk(move)); }
    public Task<MoveOutcome> StopAsync(TaskLoop loop, StopMove move, string waitingFor, CancellationToken ct) { Calls.Add("stop:" + waitingFor); return Task.FromResult(OnStop(move, waitingFor)); }
    public void Waiting(TaskLoop loop, string waitingFor) { Waits.Add(waitingFor); Calls.Add("waiting:" + waitingFor); }
    public void Ended(TaskLoop loop, LoopResult result) { Result = result; Calls.Add("ended:" + result.Outcome); }
}
