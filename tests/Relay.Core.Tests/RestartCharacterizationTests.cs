using Relay.Core.Cases;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Graceful-restart characterization of the frozen prototype runtime.
/// This is not an alpha crash gate: it disposes cleanly before reopen and does not
/// simulate process death, FailFast, or crash-injection points.
/// </summary>
public sealed class RestartCharacterizationTests : IDisposable
{
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 18, 16, 30, 0, TimeSpan.Zero));

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Graceful_restart_after_direct_input_reconstructs_the_same_case()
    {
        _tmp.Root.EnsureLayout(_clock);
        string caseId;
        string objectId;

        using (var diagnostics = Diag("restart-a"))
        using (var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics))
        {
            var started = runtime.StartDirectCase("recover me after restart");
            caseId = started.Id;
            var input = runtime.Cases.LoadEvents(caseId).Single(e => e.Type == CaseEventTypes.UserInput);
            objectId = input.Payload.GetProperty("objectId").GetString()!;
        }

        _clock.Advance(TimeSpan.FromSeconds(5));
        using (var diagnostics = Diag("restart-b"))
        using (var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics))
        {
            var resumed = runtime.GetCase(caseId);
            Assert.NotNull(resumed);
            Assert.Equal(caseId, resumed!.Id);
            Assert.Equal("recover me after restart", runtime.Objects.TryReadTextById(objectId));
        }
    }

    private RuntimeDiagnostics Diag(string runId)
        => new(Path.Combine(_tmp.Root.DevRunsDirectory, runId, "runtime.jsonl"), runId);
}
