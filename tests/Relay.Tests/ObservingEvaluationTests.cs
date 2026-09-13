using Relay.Core.Evaluation;
using Relay.Core.Mind;
using Relay.Tests.Support;

namespace Relay.Tests;

/// <summary>
/// Alpha gate: the observing evaluation stage — one pass against a recorded host, scored on what it raised.
/// </summary>
public class ObservingEvaluationTests
{
    private static readonly string CasesDirectory = Path.Combine(AppContext.BaseDirectory, "Evaluation", "observing");

    private static EvaluationSet ObservingSet() => EvaluationSet.Load(CasesDirectory);

    /// <summary>A scripted mind that raises what the authored observing cases expect, and waits on chatter.</summary>
    private static ScriptedMind GateMind() => new ScriptedMind().Always(req =>
    {
        var window = req.Transcript.OfType<WindowObserved>().LastOrDefault();
        var text = window is null ? "" : string.Join('\n', window.Fresh.Select(l => l.Text));
        var matters = new MindRead("a conversation", 0.3, [MindRead.NeedNone], 0.85, 0, RiskRead.None);
        if (text.Contains("weekend", StringComparison.OrdinalIgnoreCase))
            return MindStep.Of(ScriptedMind.Wait("ordinary talk"), "Nothing to keep.");
        if (text.Contains("21st", StringComparison.OrdinalIgnoreCase))
            return MindStep.Of(ScriptedMind.Raise("check", "Check the stated Atlas beta date against the stored decision of the 21st.", "#1"),
                "Raising the correction.", matters);
        if (text.Contains("decided", StringComparison.OrdinalIgnoreCase) || text.Contains("October 14", StringComparison.OrdinalIgnoreCase))
            return MindStep.Of(ScriptedMind.Raise("remember", "Keep the Atlas beta ship decision of October 14.", "#1",
                    note: "Atlas beta ships on October 14.", noteType: "decision", project: "Atlas"),
                "Keeping the decision.", matters);
        return MindStep.Of(ScriptedMind.Wait("nothing here"), "Listening on.");
    });

    [Fact]
    public async Task TheObservingStageScoresRaisesOverTheAuthoredSet()
    {
        var set = ObservingSet();
        Assert.Empty(set.Validate());
        Assert.All(set.Cases, c => Assert.True(c.IsObserving));

        var runner = new ObservingEvaluationRunner(GateMind(), _ => new MindContext { Projects = ["Atlas (id p1, slug atlas)"] }, () => Harness.T0);
        var report = await runner.RunAsync(set, CancellationToken.None);

        Assert.Empty(report.Problems);
        Assert.True(report.Passed, report.Render());
        Assert.Equal(set.Cases.Count, report.Results.Count);
        Assert.Contains(report.Results, r => r.Id.Contains("atlas-decision", StringComparison.Ordinal));
        Assert.Contains(report.Results, r => r.Id.Contains("ordinary-chatter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIncompleteObservingSetIsRefused()
    {
        var incomplete = EvaluationSet.Parse("""
            [{"id":"alone","source":"recorded","instruction":"x","window":[{"label":"#1","text":"hi"}],"expect":{"raises":["remember"]}}]
            """);
        var runner = new ObservingEvaluationRunner(GateMind(), _ => new MindContext(), () => Harness.T0);
        var report = await runner.RunAsync(incomplete, CancellationToken.None);
        Assert.False(report.Passed);
        Assert.NotEmpty(report.Problems);
    }

    [Fact]
    public async Task LiveObservingStageAgainstLocalModel()
    {
        var live = LiveModel.FromEnvironment();
        if (live is null) return;
        using var client = live.Client();
        var set = ObservingSet();
        var runner = new ObservingEvaluationRunner(new ModelMind(client), _ => new MindContext { Projects = ["Atlas (id p1, slug atlas)"] }, () => DateTimeOffset.UtcNow)
        {
            CaseTimeout = TimeSpan.FromSeconds(180),
        };
        var report = await runner.RunAsync(set, CancellationToken.None);
        live.Write("observing-live.json", report.ToJson());
        live.Write("observing-live.txt", report.Render());
        Assert.Empty(report.Problems);
        Assert.Equal(set.Cases.Count, report.Results.Count);
    }
}
