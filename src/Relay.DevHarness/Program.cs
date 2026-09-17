using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Policy;
using Relay.Core.Storage;
using Relay.Core.Time;

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
            Console.Error.WriteLine("Usage: Relay.DevHarness --data-root <path> [--run-id <id>] [--scenario slice1|slice2|slice3]");
            return 2;
        }

        return scenario switch
        {
            "slice1" => await RunSlice1Async(dataRootPath, runId),
            "slice2" => await RunSlice2Async(dataRootPath, runId),
            "slice3" => await RunSlice3Async(dataRootPath, runId),
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
                var stepped = await runtime.StepNextAsync();
                if (stepped is null || stepped.Status != CaseStatus.Waiting)
                    return Fail(summaryPath, runId, "expected waiting case after propose", sideEffects);

                var pending = runtime.GetPendingApproval(caseId)
                    ?? throw new InvalidOperationException("missing pending approval");
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
}
