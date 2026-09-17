using Relay.Core.Cases;
using Relay.Core.Search;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Slice 4: Lightshift research path — search → artifacts → delegate package → cited conclusion.
/// </summary>
public class Slice4ResearchTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 18, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task Lightshift_research_cites_stored_artifacts()
    {
        _tmp.Root.EnsureLayout(_clock);
        var search = new FakeSearchClient().Reply(
            new SearchHitResult(
                "Lightshift portfolio",
                "https://lightshift.example/sites",
                "Lightshift operates a portfolio of battery energy storage systems."),
            new SearchHitResult(
                "Industry brief",
                "https://news.example/lightshift-20",
                "The company confirmed 20 operational battery sites."));
        var fetch = new FakePageFetch()
            .Page("https://lightshift.example/sites", "Lightshift operates 20 battery sites across three regions.")
            .Page("https://news.example/lightshift-20", "Confirmation: 20 operational battery sites.");
        var delegateClient = new FakeDelegateClient();
        var research = new ResearchServices { Search = search, Fetch = fetch, Delegate = delegateClient };

        using var diagnostics = Diag("s4-lightshift");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new LightshiftResearchMind(), diagnostics, research: research);
        Assert.True(runtime.SearchAvailable);

        var started = runtime.StartDirectCase(LightshiftResearchMind.Question, CaseKind.Research);
        var waiting = await runtime.RunUntilIdleAsync(started.Id);
        Assert.Equal(CaseStatus.Waiting, waiting.Status);

        var searchOp = runtime.GetPendingApproval(started.Id)!;
        Assert.Equal(ResearchCapabilities.Search, searchOp.Capability);
        runtime.ApproveOperation(searchOp.OperationId, searchOp.CanonicalHash(), runtime.GetCase(started.Id)!.Version);
        runtime.ExecuteOperation(searchOp.OperationId);
        Assert.NotEmpty(search.Requests);
        Assert.NotEmpty(fetch.FetchedUrls);

        var afterSearch = await runtime.RunUntilIdleAsync(started.Id);
        Assert.Equal(CaseStatus.Waiting, afterSearch.Status);
        var delOp = runtime.GetPendingApproval(started.Id)!;
        Assert.Equal(ResearchCapabilities.Delegate, delOp.Capability);
        Assert.True(delOp.Arguments.ContainsKey("artifactObjectIds"));

        runtime.ApproveOperation(delOp.OperationId, delOp.CanonicalHash(), runtime.GetCase(started.Id)!.Version);
        runtime.ExecuteOperation(delOp.OperationId);
        Assert.Single(delegateClient.Requests);
        Assert.NotEmpty(delegateClient.Requests[0].ArtifactObjectIds);
        // Delegate never gets write grants — package stored separately.
        Assert.All(delegateClient.Requests[0].Sources, s => Assert.False(string.IsNullOrEmpty(s.Sha256)));

        var finished = await runtime.RunUntilIdleAsync(started.Id);
        Assert.Equal(CaseStatus.Completed, finished.Status);
        Assert.Contains("20", finished.Result ?? "");
        Assert.Contains("battery", finished.Result ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(finished.SourceRefs);
        Assert.All(finished.SourceRefs, r => Assert.StartsWith("artifact:", r));

        var packageReq = delegateClient.Requests[0];
        foreach (var id in packageReq.ArtifactObjectIds)
            Assert.NotNull(CaseTools.ReadArtifactResult(runtime.Objects, id));
    }

    [Fact]
    public async Task Insufficient_evidence_when_search_empty()
    {
        _tmp.Root.EnsureLayout(_clock);
        var research = new ResearchServices
        {
            Search = new FakeSearchClient().Empty(),
            Fetch = new FakePageFetch(),
            Delegate = new FakeDelegateClient(),
        };
        using var diagnostics = Diag("s4-empty");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new LightshiftResearchMind(), diagnostics, research: research);

        var started = runtime.StartDirectCase(LightshiftResearchMind.Question, CaseKind.Research);
        await runtime.RunUntilIdleAsync(started.Id);
        var searchOp = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(searchOp.OperationId, searchOp.CanonicalHash(), runtime.GetCase(started.Id)!.Version);
        runtime.ExecuteOperation(searchOp.OperationId);

        var finished = await runtime.RunUntilIdleAsync(started.Id);
        Assert.Equal(CaseStatus.Completed, finished.Status);
        Assert.Contains("Insufficient evidence", finished.Result ?? "");
        Assert.DoesNotContain("20 battery", finished.Result ?? "");
    }

    [Fact]
    public async Task Stale_completion_after_cancel_does_not_revive_case()
    {
        _tmp.Root.EnsureLayout(_clock);
        var research = new ResearchServices
        {
            Search = new FakeSearchClient().Reply(
                new SearchHitResult("X", "https://lightshift.example/x", "snippet")),
            Fetch = new FakePageFetch().Page("https://lightshift.example/x", "body"),
            Delegate = new FakeDelegateClient(),
        };
        using var diagnostics = Diag("s4-stale");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new LightshiftResearchMind(), diagnostics, research: research);

        var started = runtime.StartDirectCase(LightshiftResearchMind.Question, CaseKind.Research);
        await runtime.RunUntilIdleAsync(started.Id);
        var searchOp = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(searchOp.OperationId, searchOp.CanonicalHash(), runtime.GetCase(started.Id)!.Version);

        runtime.CancelCase(started.Id, "user cancelled");
        Assert.Equal(CaseStatus.Cancelled, runtime.GetCase(started.Id)!.Status);
        Assert.Equal(OperationStatus.Cancelled, runtime.GetOperation(searchOp.OperationId)!.Status);

        // Late completion after cancel: store result, do not revive.
        runtime.CompleteOperation(searchOp.OperationId, new { stale = true, note = "late search result" });
        Assert.Equal(OperationStatus.Completed, runtime.GetOperation(searchOp.OperationId)!.Status);
        Assert.Equal(CaseStatus.Cancelled, runtime.GetCase(started.Id)!.Status);

        // After cancel, not-yet-started ops must not be dispatched (intentional §4 correction).
        var started2 = runtime.StartDirectCase("Verify Lightshift sites again", CaseKind.Research);
        await runtime.RunUntilIdleAsync(started2.Id);
        var op2 = runtime.GetPendingApproval(started2.Id)!;
        runtime.ApproveOperation(op2.OperationId, op2.CanonicalHash(), runtime.GetCase(started2.Id)!.Version);
        runtime.CancelCase(started2.Id);
        var refused = runtime.DispatchOperation(op2.OperationId);
        Assert.Equal(OperationStatus.Cancelled, refused.Status);
        Assert.Equal(CaseStatus.Cancelled, runtime.GetCase(started2.Id)!.Status);

        // Late result accepted for audit without reopening.
        var late = runtime.AcceptOperationResult(op2.OperationId, new OperationApplyResult(true, "late audit result", ResultRef: null));
        Assert.Equal(OperationStatus.Completed, late.Status);
        Assert.Equal(CaseStatus.Cancelled, runtime.GetCase(started2.Id)!.Status);
    }

    [Fact]
    public async Task Propose_search_rejected_without_bound_adapter()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-nobound");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new LightshiftResearchMind(), diagnostics);
        Assert.False(runtime.SearchAvailable);

        var started = runtime.StartDirectCase(LightshiftResearchMind.Question, CaseKind.Research);
        var stepped = await runtime.StepCaseAsync(started.Id);
        Assert.Null(runtime.GetPendingApproval(started.Id));
        var events = runtime.Cases.LoadEvents(started.Id);
        Assert.Contains(events, e => e.Type == CaseEventTypes.MoveRejected);
        Assert.Equal(CaseStatus.Active, stepped.Status);
    }

    private RuntimeDiagnostics Diag(string runId)
        => new(Path.Combine(_tmp.Root.DevRunsDirectory, runId, "runtime.jsonl"), runId);

    public void Dispose() => _tmp.Dispose();
}
