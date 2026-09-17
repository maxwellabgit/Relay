using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Policy;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Slice 2 exit: “What did we decide about the Atlas beta date?” retrieves the stored decision,
/// cites it, and does not invent a new date. Also covers propose edit / reject on the same runtime.
/// </summary>
public class Slice2AtlasRecallTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 14, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task Atlas_beta_date_recall_cites_stored_decision()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, note) = local.SeedAtlasBetaDecision();

        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "slice2-recall", "runtime.jsonl"),
            "slice2-recall");

        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new AtlasRecallMind(), diagnostics, local: local);

        var started = runtime.StartDirectCase(AtlasRecallMind.Question);
        var finished = await runtime.RunUntilIdleAsync(started.Id);

        Assert.Equal(CaseStatus.Completed, finished.Status);
        Assert.Equal(CaseLocalContext.AtlasBetaBody, finished.Result);
        Assert.DoesNotContain("October 15", finished.Result);
        Assert.DoesNotContain("November", finished.Result ?? "");
        Assert.Contains(finished.SourceRefs, r => r.Contains(note.Id, StringComparison.Ordinal));
        Assert.Contains(finished.SourceRefs, r => r.Contains(project.Id, StringComparison.Ordinal));
        Assert.Contains(finished.SourceRefs, r => r.Contains(CaseLocalContext.AtlasSourceEventId, StringComparison.Ordinal));

        var events = runtime.Cases.LoadEvents(started.Id);
        Assert.Contains(events, e => e.Type == CaseEventTypes.ToolCalled && e.Payload.GetProperty("tool").GetString() == CaseTools.LocalSearch);
        Assert.Contains(events, e => e.Type == CaseEventTypes.ToolResult && e.Payload.GetProperty("tool").GetString() == CaseTools.LocalSearch);
        Assert.Contains(events, e => e.Type == CaseEventTypes.ToolCalled && e.Payload.GetProperty("tool").GetString() == CaseTools.ReadNote);
        Assert.Contains(events, e => e.Type == CaseEventTypes.ToolResult && e.Payload.GetProperty("tool").GetString() == CaseTools.ReadNote);
        Assert.Contains(events, e => e.Type == CaseEventTypes.CaseCompleted);

        var feed = runtime.Projections.ListFeedItems(started.Id);
        Assert.True(feed.Count >= 4, $"expected feed items for ask/search/read/answer, got {feed.Count}");
        Assert.Contains(feed, f => f.Level == "finding");
        Assert.Contains(feed, f => f.Text.Contains("Searching", StringComparison.OrdinalIgnoreCase)
            || f.Text.Contains("Reading", StringComparison.OrdinalIgnoreCase)
            || f.Text.Contains("Answering", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Edit_proposal_requires_new_hash_and_reapproval()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        local.SeedAtlasBetaDecision();

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["name"] = JsonSerializer.SerializeToElement("Side Project"),
            ["slug"] = JsonSerializer.SerializeToElement("side-project"),
        };
        var mind = new ScriptedProposeMind(Actions.CreateProject, args, "slice2-create-project");

        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "slice2-edit", "runtime.jsonl"),
            "slice2-edit");
        var sideEffects = 0;
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics, () => sideEffects++, local);

        var started = runtime.StartDirectCase("Create a side project");
        var waiting = await runtime.RunUntilIdleAsync(started.Id);
        Assert.Equal(CaseStatus.Waiting, waiting.Status);

        var pending = runtime.GetPendingApproval(started.Id)!;
        var originalHash = pending.CanonicalHash();

        var editedArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["name"] = JsonSerializer.SerializeToElement("Side Project Renamed"),
            ["slug"] = JsonSerializer.SerializeToElement("side-project"),
        };
        var edited = runtime.EditOperation(pending.OperationId, editedArgs);
        Assert.NotEqual(originalHash, edited.CanonicalHash());
        Assert.Equal(OperationStatus.AwaitingApproval, edited.Status);
        Assert.Equal(OperationStatus.Cancelled, runtime.GetOperation(pending.OperationId)!.Status);

        // Old hash must not approve the new envelope.
        var caseAfterEdit = runtime.GetCase(started.Id)!;
        Assert.Throws<InvalidOperationException>(() =>
            runtime.ApproveOperation(edited.OperationId, originalHash, caseAfterEdit.Version));

        var approved = runtime.ApproveOperation(edited.OperationId, edited.CanonicalHash(), caseAfterEdit.Version);
        Assert.Equal(OperationStatus.Approved, approved.Status);
        runtime.ExecuteOperation(edited.OperationId);
        Assert.Equal(1, sideEffects);
        Assert.NotNull(runtime.Local.Registry.FindActive("side-project"));
        Assert.Equal("Side Project Renamed", runtime.Local.Registry.FindActive("side-project")!.Name);
    }

    [Fact]
    public async Task Reject_allows_mind_to_stop_without_write()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["name"] = JsonSerializer.SerializeToElement("Rejected Project"),
            ["slug"] = JsonSerializer.SerializeToElement("rejected-project"),
        };
        var mind = new ScriptedProposeMind(Actions.CreateProject, args, "slice2-reject");

        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "slice2-reject", "runtime.jsonl"),
            "slice2-reject");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics, local: local);

        var started = runtime.StartDirectCase("Create something we will reject");
        await runtime.RunUntilIdleAsync(started.Id);
        var pending = runtime.GetPendingApproval(started.Id)!;
        runtime.RejectOperation(pending.OperationId, "not now");

        Assert.Equal(OperationStatus.Denied, runtime.GetOperation(pending.OperationId)!.Status);
        var after = await runtime.RunUntilIdleAsync(started.Id);
        Assert.Equal(CaseStatus.Completed, after.Status);
        Assert.Contains("will not make that change", after.Result ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Null(runtime.Local.Registry.FindActive("rejected-project"));
    }

    public void Dispose() => _tmp.Dispose();
}
