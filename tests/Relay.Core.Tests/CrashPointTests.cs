using Relay.Core.Cases;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>Crash point: process death after a committed direct-input transition still reconstructs one case.</summary>
public sealed class CrashPointTests : IDisposable
{
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 18, 16, 30, 0, TimeSpan.Zero));

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Restart_after_direct_input_reconstructs_the_same_case()
    {
        _tmp.Root.EnsureLayout(_clock);
        string caseId;
        string objectId;

        using (var diagnostics = Diag("crash-a"))
        using (var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics))
        {
            var started = runtime.StartDirectCase("recover me after crash");
            caseId = started.Id;
            var input = runtime.Cases.LoadEvents(caseId).Single(e => e.Type == CaseEventTypes.UserInput);
            objectId = input.Payload.GetProperty("objectId").GetString()!;
        }

        _clock.Advance(TimeSpan.FromSeconds(5));
        using (var diagnostics = Diag("crash-b"))
        using (var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics))
        {
            var resumed = runtime.GetCase(caseId);
            Assert.NotNull(resumed);
            Assert.Equal(caseId, resumed!.Id);
            Assert.Equal("recover me after crash", runtime.Objects.TryReadTextById(objectId));
        }
    }

    private RuntimeDiagnostics Diag(string runId)
        => new(Path.Combine(_tmp.Root.DevRunsDirectory, runId, "runtime.jsonl"), runId);
}
