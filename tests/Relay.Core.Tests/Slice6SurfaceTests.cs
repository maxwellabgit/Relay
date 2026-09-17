using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Slice 6: one feed + one composer drive core scenarios through <see cref="IRelaySurface"/>.
/// </summary>
public class Slice6SurfaceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task Composer_submit_approve_shows_on_single_feed()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s6-composer");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => { });
        IRelaySurface surface = new CaseRuntimeSurface(runtime, _clock);

        var snap0 = surface.Snapshot();
        Assert.False(snap0.Listening);
        Assert.Equal(ModelHealthView.Unavailable, snap0.ModelHealth.Status);
        Assert.Empty(snap0.PendingApprovals);

        var submitted = surface.SubmitComposer("slice6: run the counter once");
        Assert.True(submitted.Ok, submitted.Error);
        Assert.NotNull(submitted.CaseId);

        var idle = await surface.RunUntilIdleAsync(submitted.CaseId);
        Assert.True(idle.Ok, idle.Error);

        var snap1 = surface.Snapshot();
        Assert.NotEmpty(snap1.Feed);
        Assert.Contains(snap1.Feed, f => f.Text.Contains("New direct ask", StringComparison.OrdinalIgnoreCase)
            || f.Text.Contains("Approval needed", StringComparison.OrdinalIgnoreCase));
        Assert.Single(snap1.PendingApprovals);
        Assert.Equal(submitted.CaseId, snap1.ComposerCaseId);

        var card = snap1.PendingApprovals[0];
        var approved = surface.ApproveOperation(card.OperationId, card.EnvelopeHash, card.CaseVersion);
        Assert.True(approved.Ok, approved.Error);
        runtime.ExecuteOperation(card.OperationId);

        await surface.RunUntilIdleAsync(submitted.CaseId);
        var snap2 = surface.Snapshot();
        Assert.Empty(snap2.PendingApprovals);
        Assert.Contains(snap2.Feed, f => f.Text.Contains("Approved", StringComparison.OrdinalIgnoreCase)
            || f.Text.Contains("Side effect", StringComparison.OrdinalIgnoreCase)
            || f.Level == "persistent");
    }

    [Fact]
    public async Task Toggle_listening_and_reject_edit_cancel_via_surface()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s6-listen");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        IRelaySurface surface = new CaseRuntimeSurface(runtime, _clock);

        var on = surface.ToggleListening();
        Assert.True(on.Ok, on.Error);
        Assert.True(surface.Snapshot().Listening);
        Assert.Equal(on.CaseId, surface.Snapshot().ListeningCaseId);

        var off = surface.ToggleListening();
        Assert.True(off.Ok, off.Error);
        Assert.False(surface.Snapshot().Listening);

        var submitted = surface.SubmitComposer("need approval then reject");
        await surface.RunUntilIdleAsync(submitted.CaseId);
        var card = surface.Snapshot().PendingApprovals.Single();

        var edited = surface.EditOperation(card.OperationId, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capability"] = JsonSerializer.SerializeToElement(ScriptedCaseMind.DefaultCapability),
            ["label"] = JsonSerializer.SerializeToElement("edited-label"),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement(ScriptedCaseMind.DefaultIdempotencyKey + ":edit-test"),
        });
        Assert.True(edited.Ok, edited.Error);
        Assert.NotEqual(card.OperationId, edited.OperationId);

        var newCard = surface.Snapshot().PendingApprovals.Single(p => p.OperationId == edited.OperationId);
        var rejected = surface.RejectOperation(newCard.OperationId, "user said no");
        Assert.True(rejected.Ok, rejected.Error);

        var cancelled = surface.CancelCase(submitted.CaseId!, "done testing");
        Assert.True(cancelled.Ok, cancelled.Error);
        Assert.Equal(CaseStatus.Cancelled, runtime.GetCase(submitted.CaseId!)!.Status);
    }

    private static RuntimeDiagnostics Diag(string runId)
    {
        var path = Path.Combine(Path.GetTempPath(), "relay-s6-" + runId + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        return new RuntimeDiagnostics(path, runId);
    }

    public void Dispose() => _tmp.Dispose();
}
