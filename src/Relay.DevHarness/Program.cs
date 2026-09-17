using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.Storage;
using Relay.Core.Time;
using Relay.Core.Tools;
using Relay.Core.Usage;

namespace Relay.DevHarness;

/// <summary>
/// Console harness for Slice 1 recovery and Slice 2 Atlas recall scenarios.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? dataRootPath = null;
        string runId = "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        string scenario = "slice1";

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-root" && i + 1 < args.Length) dataRootPath = args[++i];
            else if (args[i] == "--run-id" && i + 1 < args.Length) runId = args[++i];
            else if (args[i] == "--scenario" && i + 1 < args.Length) scenario = args[++i];
        }

        if (string.IsNullOrWhiteSpace(dataRootPath))
        {
            Console.Error.WriteLine("Usage: Relay.DevHarness --data-root <path> [--run-id <id>] [--scenario slice1|slice2|slice3|slice4|slice5|slice6|slice7]");
            return 2;
        }

        return scenario switch
        {
            "slice1" => await RunSlice1Async(dataRootPath, runId),
            "slice2" => await RunSlice2Async(dataRootPath, runId),
            "slice3" => await RunSlice3Async(dataRootPath, runId),
            "slice4" => await RunSlice4Async(dataRootPath, runId),
            "slice5" => await RunSlice5Async(dataRootPath, runId),
            "slice6" => await RunSlice6Async(dataRootPath, runId),
            "slice7" => RunSlice7(dataRootPath, runId),
            _ => FailUsage($"Unknown scenario '{scenario}'."),
        };
    }

    private static async Task<int> RunSlice1Async(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);

        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var diagnosticsPath = Path.Combine(runDir, "runtime.jsonl");
        var summaryPath = Path.Combine(runDir, "summary.json");

        var sideEffects = 0;
        string caseId;
        string operationId;
        string envelopeHash;

        using (var diagnostics = new RuntimeDiagnostics(diagnosticsPath, runId))
        {
            using (var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics, () => sideEffects++))
            {
                var started = runtime.StartDirectCase("harness: slice1 side effect");
                caseId = started.Id;
                // Step this case explicitly so a shared data root's ready queue cannot steal the turn.
                var stepped = await runtime.StepCaseAsync(caseId);
                if (stepped.Id != caseId || stepped.Status != CaseStatus.Waiting)
                    return Fail(summaryPath, runId, $"expected waiting case after propose, got {stepped.Id}/{stepped.Status}", sideEffects);

                var pending = runtime.GetPendingApproval(caseId);
                if (pending is null)
                    return Fail(summaryPath, runId, "missing pending approval after propose", sideEffects);
                operationId = pending.OperationId;
                envelopeHash = pending.CanonicalHash();
                runtime.SuspendAll();
            }

            clock.Advance(TimeSpan.FromMinutes(1));

            using (var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics, () => sideEffects++))
            {
                var resumed = runtime.GetCase(caseId)
                    ?? throw new InvalidOperationException("case missing after restart");
                if (resumed.Status != CaseStatus.Waiting)
                    return Fail(summaryPath, runId, $"expected waiting after restart, got {resumed.Status}", sideEffects);

                runtime.ApproveOperation(operationId, envelopeHash, resumed.Version);
                runtime.ExecuteOperation(operationId);
                runtime.CompleteOperation(operationId, new { duplicate = true });
                runtime.ExecuteOperation(operationId);

                if (sideEffects != 1)
                    return Fail(summaryPath, runId, $"expected sideEffects=1, got {sideEffects}", sideEffects);

                return Ok(summaryPath, new
                {
                    ok = true,
                    scenario = "slice1",
                    runId,
                    caseId,
                    operationId,
                    sideEffects,
                    diagnostics = diagnosticsPath,
                });
            }
        }
    }

    private static async Task<int> RunSlice2Async(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 17, 14, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);

        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var diagnosticsPath = Path.Combine(runDir, "runtime.jsonl");
        var summaryPath = Path.Combine(runDir, "summary.json");

        var local = new CaseLocalContext(root, clock);
        var (project, note) = local.SeedAtlasBetaDecision();

        using var diagnostics = new RuntimeDiagnostics(diagnosticsPath, runId);
        using var runtime = CaseRuntime.Open(root, clock, new AtlasRecallMind(), diagnostics, local: local);

        var started = runtime.StartDirectCase(AtlasRecallMind.Question);
        var finished = await runtime.RunUntilIdleAsync(started.Id);

        if (finished.Status != CaseStatus.Completed)
            return Fail(summaryPath, runId, $"expected completed, got {finished.Status}", 0);
        if (!string.Equals(finished.Result, CaseLocalContext.AtlasBetaBody, StringComparison.Ordinal))
            return Fail(summaryPath, runId, $"unexpected answer: {finished.Result}", 0);
        if (finished.SourceRefs.Count == 0)
            return Fail(summaryPath, runId, "missing citations", 0);

        // Also exercise propose → edit → approve → execute on the same data root.
        var proposeMind = new ScriptedProposeMind(
            Actions.CreateProject,
            new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("Harness Extra"),
                ["slug"] = JsonSerializer.SerializeToElement("harness-extra"),
            },
            "harness-extra-project");
        using var proposeRuntime = CaseRuntime.Open(root, clock, proposeMind, diagnostics, local: local);
        var proposeCase = proposeRuntime.StartDirectCase("Create harness-extra project");
        await proposeRuntime.RunUntilIdleAsync(proposeCase.Id);
        var pending = proposeRuntime.GetPendingApproval(proposeCase.Id)!;
        var edited = proposeRuntime.EditOperation(pending.OperationId, new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("Harness Extra"),
            ["slug"] = JsonSerializer.SerializeToElement("harness-extra"),
        });
        var afterEdit = proposeRuntime.GetCase(proposeCase.Id)!;
        proposeRuntime.ApproveOperation(edited.OperationId, edited.CanonicalHash(), afterEdit.Version);
        proposeRuntime.ExecuteOperation(edited.OperationId);

        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "slice2",
            runId,
            caseId = started.Id,
            answer = finished.Result,
            sourceRefs = finished.SourceRefs,
            noteId = note.Id,
            projectId = project.Id,
            feedCount = runtime.Projections.ListFeedItems(started.Id).Count,
            diagnostics = diagnosticsPath,
        });
    }

    private static async Task<int> RunSlice3Async(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);

        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var diagnosticsPath = Path.Combine(runDir, "runtime.jsonl");
        var summaryPath = Path.Combine(runDir, "summary.json");

        var local = new CaseLocalContext(root, clock);
        var (project, note) = local.SeedAtlasBetaDecision();

        using var diagnostics = new RuntimeDiagnostics(diagnosticsPath, runId);
        var mind = new OriginRoutingMind(new ListeningScriptedMind(project.Id, note.Id), new SimpleDirectMind());
        string listenId;
        string childId;
        string? pendingOpId = null;

        using (var runtime = CaseRuntime.Open(root, clock, mind, diagnostics, local: local))
        {
            var listening = runtime.StartListening();
            listenId = listening.Id;

            runtime.IngestSegment("API means Application Programming Interface", clock.UtcNow);
            await runtime.RunUntilIdleAsync(listenId);

            runtime.IngestSegment("We should capture the onboarding checklist idea.", clock.UtcNow.AddSeconds(10));
            var afterRaise = await runtime.RunUntilIdleAsync(listenId);
            if (afterRaise.ChildCaseIds.Count == 0)
                return Fail(summaryPath, runId, "expected raise_task child", 0);
            childId = afterRaise.ChildCaseIds[0];

            runtime.IngestSegment("Actually, the Atlas beta ships on October 21.", clock.UtcNow.AddSeconds(20));
            var afterCorrect = await runtime.RunUntilIdleAsync(listenId);
            var pending = runtime.GetPendingApproval(listenId);
            if (pending is null)
                return Fail(summaryPath, runId, "expected correction proposal", 0);
            pendingOpId = pending.OperationId;

            var direct = runtime.StartDirectCase("Status while listening?");
            var answered = await runtime.RunUntilIdleAsync(direct.Id);
            if (answered.Status != CaseStatus.Completed)
                return Fail(summaryPath, runId, "direct ask failed while observed pending", 0);

            runtime.SuspendAll();
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        using (var runtime = CaseRuntime.Open(root, clock, mind, diagnostics, local: local))
        {
            var resumed = runtime.GetListeningCase();
            if (resumed is null || resumed.Id != listenId)
                return Fail(summaryPath, runId, "listening case missing after restart", 0);
            if (runtime.GetPendingApproval(listenId)?.OperationId != pendingOpId)
                return Fail(summaryPath, runId, "pending approval lost after restart", 0);

            var segments = runtime.Intake.LoadRecentSegments(listenId);
            if (segments.Count < 3)
                return Fail(summaryPath, runId, $"expected >=3 segments, got {segments.Count}", 0);

            return Ok(summaryPath, new
            {
                ok = true,
                scenario = "slice3",
                runId,
                listenId,
                childId,
                pendingOpId,
                segmentCount = segments.Count,
                feedCount = runtime.Projections.ListFeedItems(listenId).Count,
                diagnostics = diagnosticsPath,
            });
        }
    }

    private static async Task<int> RunSlice4Async(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 17, 18, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);

        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var diagnosticsPath = Path.Combine(runDir, "runtime.jsonl");
        var summaryPath = Path.Combine(runDir, "summary.json");

        var search = new HarnessSearchClient();
        var fetch = new HarnessPageFetch();
        var del = new HarnessDelegateClient();
        var research = new ResearchServices { Search = search, Fetch = fetch, Delegate = del };

        using var diagnostics = new RuntimeDiagnostics(diagnosticsPath, runId);
        using var runtime = CaseRuntime.Open(root, clock, new LightshiftResearchMind(), diagnostics, research: research);

        var started = runtime.StartDirectCase(LightshiftResearchMind.Question, CaseKind.Research);
        await runtime.RunUntilIdleAsync(started.Id);
        var searchOp = runtime.GetPendingApproval(started.Id);
        if (searchOp is null) return Fail(summaryPath, runId, "missing search approval", 0);
        runtime.ApproveOperation(searchOp.OperationId, searchOp.CanonicalHash(), runtime.GetCase(started.Id)!.Version);
        runtime.ExecuteOperation(searchOp.OperationId);

        await runtime.RunUntilIdleAsync(started.Id);
        var delOp = runtime.GetPendingApproval(started.Id);
        if (delOp is null) return Fail(summaryPath, runId, "missing delegate approval", 0);
        runtime.ApproveOperation(delOp.OperationId, delOp.CanonicalHash(), runtime.GetCase(started.Id)!.Version);
        runtime.ExecuteOperation(delOp.OperationId);

        var finished = await runtime.RunUntilIdleAsync(started.Id);
        if (finished.Status != CaseStatus.Completed)
            return Fail(summaryPath, runId, $"expected completed, got {finished.Status}", 0);
        if (finished.SourceRefs.Count == 0 || finished.Result is null || !finished.Result.Contains("20"))
            return Fail(summaryPath, runId, $"bad answer/citations: {finished.Result}", 0);

        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "slice4",
            runId,
            caseId = started.Id,
            answer = finished.Result,
            sourceRefs = finished.SourceRefs,
            searchHost = search.Host,
            diagnostics = diagnosticsPath,
        });
    }

    private static async Task<int> RunSlice5Async(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);

        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var diagnosticsPath = Path.Combine(runDir, "runtime.jsonl");
        var summaryPath = Path.Combine(runDir, "summary.json");

        var host = new HostFunctions(() => clock.UtcNow);
        var tools = new ToolServices
        {
            Runner = new InProcessJintToolRunner(host, () => clock.UtcNow),
            Drafter = new WorldClockToolDrafter(),
            Clock = () => clock.UtcNow,
        };

        using var diagnostics = new RuntimeDiagnostics(diagnosticsPath, runId);
        using var runtime = CaseRuntime.Open(root, clock, new WorldClockToolMind(), diagnostics, tools: tools);

        var tokyo = runtime.StartDirectCase(WorldClockToolMind.TokyoAsk, CaseKind.Answer);
        await runtime.RunUntilIdleAsync(tokyo.Id);
        var promote = runtime.GetPendingApproval(tokyo.Id);
        if (promote is null) return Fail(summaryPath, runId, "missing promote approval", 0);
        if (promote.Arguments["name"].GetString() != "world_clock")
            return Fail(summaryPath, runId, "expected world_clock name on card", 0);
        runtime.ApproveOperation(promote.OperationId, promote.CanonicalHash(), runtime.GetCase(tokyo.Id)!.Version);
        runtime.ExecuteOperation(promote.OperationId);
        var tokyoDone = await runtime.RunUntilIdleAsync(tokyo.Id);
        if (tokyoDone.Status != CaseStatus.Completed || tokyoDone.Result is null || !tokyoDone.Result.Contains("21:00"))
            return Fail(summaryPath, runId, $"tokyo answer bad: {tokyoDone.Result}", 0);

        var ktm = runtime.StartDirectCase("What time is it in Kathmandu?", CaseKind.Answer);
        var ktmDone = await runtime.RunUntilIdleAsync(ktm.Id);
        if (ktmDone.Status != CaseStatus.Completed || runtime.GetPendingApproval(ktm.Id) is not null)
            return Fail(summaryPath, runId, "kathmandu rebuilt or failed", 0);

        var lon = runtime.StartDirectCase("What time is it in London?", CaseKind.Answer);
        var lonDone = await runtime.RunUntilIdleAsync(lon.Id);
        if (lonDone.Status != CaseStatus.Completed)
            return Fail(summaryPath, runId, "london failed", 0);

        var changeSet = runtime.Tools!.Changes.All().Single(c => c.Kind == "tool" && !c.Reverted);
        var revertResult = runtime.Tools.Changes.Revert(changeSet.ChangeSetId, "harness rollback", clock.UtcNow);
        if (!revertResult.Ok || runtime.Tools.Tools.IsPromoted("world_clock"))
            return Fail(summaryPath, runId, "revert did not remove world_clock", 0);

        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "slice5",
            runId,
            tokyo = tokyoDone.Result,
            kathmandu = ktmDone.Result,
            london = lonDone.Result,
            reverted = true,
            diagnostics = diagnosticsPath,
        });
    }

    private static async Task<int> RunSlice6Async(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);

        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var diagnosticsPath = Path.Combine(runDir, "runtime.jsonl");
        var summaryPath = Path.Combine(runDir, "summary.json");

        using var diagnostics = new RuntimeDiagnostics(diagnosticsPath, runId);
        using var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics, () => { });
        IRelaySurface surface = new CaseRuntimeSurface(runtime, clock);

        // Ensure listening is on, then off — shared data roots may already be listening.
        if (!surface.Snapshot().Listening)
        {
            var listen = surface.ToggleListening();
            if (!listen.Ok || !surface.Snapshot().Listening)
                return Fail(summaryPath, runId, "toggle listening failed", 0);
        }
        var stop = surface.ToggleListening();
        if (!stop.Ok || surface.Snapshot().Listening)
            return Fail(summaryPath, runId, "stop listening failed", 0);

        var submitted = surface.SubmitComposer("harness slice6 side effect");
        if (!submitted.Ok) return Fail(summaryPath, runId, submitted.Error ?? "submit failed", 0);
        await surface.RunUntilIdleAsync(submitted.CaseId);
        var snap = surface.Snapshot();
        var mine = snap.PendingApprovals.Where(a => a.CaseId == submitted.CaseId).ToList();
        if (mine.Count != 1 || snap.Feed.Count == 0)
            return Fail(summaryPath, runId, $"expected one approval for composer case (mine={mine.Count}, feed={snap.Feed.Count}, allPending={snap.PendingApprovals.Count})", 0);

        var card = mine[0];
        var approved = surface.ApproveOperation(card.OperationId, card.EnvelopeHash, card.CaseVersion);
        if (!approved.Ok) return Fail(summaryPath, runId, approved.Error ?? "approve failed", 0);
        runtime.ExecuteOperation(card.OperationId);
        await surface.RunUntilIdleAsync(submitted.CaseId);

        var final = surface.Snapshot();
        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "slice6",
            runId,
            feedCount = final.Feed.Count,
            pendingApprovals = final.PendingApprovals.Count(a => a.CaseId == submitted.CaseId),
            modelHealth = final.ModelHealth.Status,
            listening = final.Listening,
            caseId = submitted.CaseId,
            diagnostics = diagnosticsPath,
        });
    }

    private static int RunSlice7(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);

        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        var summaryPath = Path.Combine(runDir, "summary.json");

        var store = new FrictionEvidenceStore(root);
        for (var i = 0; i < 3; i++)
        {
            store.Capture(
                FrictionKinds.FailedCapability,
                clock.UtcNow.AddMinutes(i),
                "need:world_clock",
                "repeated capability gap",
                "case-" + i,
                ["example-" + i]);
        }

        var proposal = store.Suggest(clock.UtcNow.AddHours(1));
        var problems = proposal.Validate();
        if (proposal.Kind != ImprovementKinds.Tool || problems.Count > 0)
            return Fail(summaryPath, runId, "expected valid tool improvement: " + string.Join("; ", problems), 0);

        // Insufficient evidence → no_change
        var emptyRoot = new DataRoot(Path.Combine(dataRootPath, "empty-friction"));
        emptyRoot.EnsureLayout(clock);
        var noChange = new FrictionEvidenceStore(emptyRoot).Suggest(clock.UtcNow);
        if (noChange.Kind != ImprovementKinds.NoChange)
            return Fail(summaryPath, runId, "expected no_change when evidence is thin", 0);

        return Ok(summaryPath, new
        {
            ok = true,
            scenario = "slice7",
            runId,
            kind = proposal.Kind,
            frictionKind = proposal.FrictionKind,
            title = proposal.Title,
            examples = proposal.Examples.Count,
            evaluationCases = proposal.EvaluationCases.Count,
            successMetric = proposal.SuccessMetric,
            reversionPlan = proposal.ReversionPlan,
            proposalId = proposal.ProposalId,
            noChangeKind = noChange.Kind,
        });
    }

    private static int Ok(string summaryPath, object summary)
    {
        AtomicFile.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, RelayJson.Indented));
        Console.WriteLine(JsonSerializer.Serialize(summary, RelayJson.Compact));
        return 0;
    }

    private static int Fail(string summaryPath, string runId, string error, int sideEffects)
    {
        var summary = new { ok = false, runId, error, sideEffects };
        AtomicFile.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, RelayJson.Indented));
        Console.Error.WriteLine(error);
        return 1;
    }

    private static int FailUsage(string error)
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    private sealed class HarnessClock : IClock
    {
        public HarnessClock(DateTimeOffset start) => UtcNow = start;
        public DateTimeOffset UtcNow { get; set; }
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class HarnessSearchClient : ISearchClient
    {
        public string Host => "search.test";
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SearchResponse(true,
            [
                new SearchHitResult("Lightshift portfolio", "https://lightshift.example/sites", "Battery portfolio overview."),
                new SearchHitResult("Industry brief", "https://news.example/lightshift-20", "20 operational battery sites."),
            ], 10, null, 200));
    }

    private sealed class HarnessPageFetch : IPageFetch
    {
        public IReadOnlyList<string> AllowedHosts { get; } = ["lightshift.example", "news.example"];
        public Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(new PageFetchResult(true, url, "Lightshift operates 20 battery sites. Source: " + url, null, 2));
    }

    private sealed class HarnessDelegateClient : IDelegateClient
    {
        public string Profile => "research-delegate";
        public Task<DelegateResponse> CompleteAsync(DelegateRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new DelegateResponse(true,
                "Based on stored source artifacts, Lightshift operates 20 battery energy storage sites.",
                null, 12));
    }
}
