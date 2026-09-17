using Relay.Core.Cases;
using Relay.Core.Composition;
using Relay.Core.Evidence;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>§12 headless surface + composition factory tests.</summary>
public class SurfaceCompositionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Snapshot_exposes_listening_hosted_capture_health_and_retention()
    {
        _tmp.Root.EnsureLayout(_clock);
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "s12", "runtime.jsonl"), "s12");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        var retention = new RetentionService(_tmp.Root, evidence, runtime.Objects, _clock);
        IRelaySurface surface = new CaseRuntimeSurface(runtime, _clock, retention: retention);

        surface.ToggleListening();
        surface.SetHostedProcessing(true, "grant-1");
        runtime.IngestSegment("hello surface", _clock.UtcNow);

        var snap = surface.Snapshot();
        Assert.True(snap.Listening);
        Assert.True(snap.HostedProcessingEnabled);
        Assert.Equal("grant-1", snap.HostedGrantId);
        Assert.NotNull(snap.Capture);
        Assert.True(snap.Capture!.PendingSegmentCount >= 1 || snap.Capture.PendingWindowCount >= 1);
        Assert.NotNull(snap.ServiceHealth);
        Assert.NotNull(snap.Retention);
        Assert.Equal(30, snap.Retention!.DefaultTtlDays);
        Assert.NotEmpty(snap.Cases!);
    }

    [Fact]
    public async Task Composer_attaches_to_waiting_clarification_case()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "s12b", "runtime.jsonl"), "s12b");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        IRelaySurface surface = new CaseRuntimeSurface(runtime, _clock);

        var c = runtime.StartDirectCase("Need clarification?");
        await runtime.StepCaseAsync(c.Id);
        var waiting = runtime.GetCase(c.Id)!;
        Assert.Equal(CaseStatus.Waiting, waiting.Status);

        var result = surface.SubmitComposer("The answer is Tuesday");
        Assert.True(result.Ok, result.Error);
        Assert.Equal(c.Id, result.CaseId);
        Assert.Contains(surface.Snapshot().Feed, f => f.Text.Contains("Tuesday", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_mode_rejects_scripted_minds()
    {
        _tmp.Root.EnsureLayout(_clock);
        Assert.Throws<InvalidOperationException>(() => RelayCompositionFactory.Create(new RelayCompositionOptions
        {
            Root = _tmp.Root,
            Clock = _clock,
            RunId = "prod-reject",
            ProviderMode = RelayProviderMode.Production,
            Mind = new ScriptedCaseMind(),
            AllowScriptedMinds = false,
        }));
    }

    [Fact]
    public void Fixture_composition_builds_surface_and_replay_refuses_outbound_research()
    {
        _tmp.Root.EnsureLayout(_clock);
        var composed = RelayCompositionFactory.Create(new RelayCompositionOptions
        {
            Root = _tmp.Root,
            Clock = _clock,
            RunId = "fixture-ok",
            ProviderMode = RelayProviderMode.Replay,
            Mind = new ScriptedCaseMind(),
            AllowScriptedMinds = true,
            Research = new ResearchServices
            {
                Search = new Support.FakeSearchClient(),
            },
        });
        using (composed.Diagnostics)
        using (composed.Runtime)
        {
            Assert.Equal(RelayProviderMode.Replay, composed.ProviderMode);
            Assert.False(composed.Runtime.Research?.SearchAvailable ?? false);
            var snap = composed.Surface.Snapshot();
            Assert.NotNull(snap);
        }
    }

    [Fact]
    public void Diagnostics_omit_raw_private_content_fields()
    {
        var props = typeof(RuntimeDiagnosticEvent).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Body", props);
        Assert.DoesNotContain("Text", props);
        Assert.DoesNotContain("Secret", props);
        Assert.DoesNotContain("ApiKey", props);
        Assert.Contains("ContentHash", props);
        Assert.Contains("DecisionId", props);
        Assert.Contains("WindowId", props);
    }
}
