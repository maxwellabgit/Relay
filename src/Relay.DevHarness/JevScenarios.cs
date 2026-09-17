using System.Text.Json;
using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Evidence;
using Relay.Core.Judgments;
using Relay.Core.Policy;
using Relay.Core.Processes;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.DevHarness;

/// <summary>§13 named Jev harness scenarios (fixture provider).</summary>
internal static class JevScenarios
{
    public static readonly string[] Names =
    [
        "jev-atlas-stream",
        "jev-direct-recall",
        "jev-local-only",
        "jev-outage-recovery",
        "jev-concurrency",
        "jev-cancel-stale",
        "jev-research-evidence",
        "jev-retention",
        "jev-capability-growth",
        "jev-projection-rebuild",
    ];

    public static async Task<int> RunAsync(string scenario, string dataRootPath, string runId, string provider)
    {
        if (!string.Equals(provider, "fixture", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider, "replay", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Provider '{provider}' not supported in harness yet; use fixture.");
            return 2;
        }

        return scenario switch
        {
            "jev-atlas-stream" => await AtlasStream(dataRootPath, runId),
            "jev-direct-recall" => DirectRecall(dataRootPath, runId),
            "jev-local-only" => LocalOnly(dataRootPath, runId),
            "jev-outage-recovery" => await OutageRecovery(dataRootPath, runId),
            "jev-concurrency" => await Concurrency(dataRootPath, runId),
            "jev-cancel-stale" => await CancelStale(dataRootPath, runId),
            "jev-research-evidence" => await ResearchEvidence(dataRootPath, runId),
            "jev-retention" => Retention(dataRootPath, runId),
            "jev-capability-growth" => CapabilityGrowth(dataRootPath, runId),
            "jev-projection-rebuild" => await ProjectionRebuild(dataRootPath, runId),
            _ => 2,
        };
    }

    private static async Task<int> AtlasStream(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        using var diagnostics = Diag(root, runId);
        var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
        using var runtime = CaseRuntime.Open(root, clock, mind, diagnostics);
        runtime.StartListening();
        runtime.IngestSegment("API means Application Programming Interface", clock.UtcNow);
        var listenId = runtime.GetListeningCase()!.Id;
        await runtime.RunUntilIdleAsync(listenId);
        var feed = runtime.Projections.ListFeedItems(listenId);
        if (!feed.Any(f => f.Text.Contains("API means", StringComparison.Ordinal)))
            return Fail(summaryPath, runId, "expected acronym feed item");
        return Ok(summaryPath, new { ok = true, scenario = "jev-atlas-stream", runId, listenId });
    }

    private static int DirectRecall(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        var older = ("s1", "Atlas ships Oct 1", clock.UtcNow, (string?)null);
        var newer = ("s2", "Atlas ships Nov 1", clock.UtcNow.AddDays(1), (string?)null);
        var blocked = RecallFactPolicy.Resolve([older, newer], null, null);
        if (blocked.Answered || blocked.BlockReason != "newer_not_auto_accepted")
            return Fail(summaryPath, runId, "expected newer_not_auto_accepted");
        var accepted = RecallFactPolicy.Resolve(
            [older, ("s2", "Atlas ships Nov 1", clock.UtcNow.AddDays(1), "user:alice")],
            new Relay.Core.Judgments.DecisionInterpretation
            {
                DecisionId = "statement.revision_relation",
                QuestionKey = "revision_relation",
                Primitive = "choice",
                Polarity = Relay.Core.Judgments.DecisionPolarity.Positive,
                ChosenOption = "revision",
            },
            null);
        if (!accepted.Answered)
            return Fail(summaryPath, runId, "expected authority revision answer");
        return Ok(summaryPath, new { ok = true, scenario = "jev-direct-recall", runId });
    }

    private static int LocalOnly(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        var auth = new HostedAuthorization(root, clock, hostedEnabled: false);
        using var diagnostics = Diag(root, runId);
        using var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics, hosted: auth);
        runtime.StartListening();
        var seg = runtime.IngestSegment("private local only talk", clock.UtcNow);
        var artifact = auth.Evidence.TryLoad("seg:" + seg.SegmentId);
        if (artifact is null || artifact.Restriction != ContentRestriction.LocalOnly)
            return Fail(summaryPath, runId, "expected local_only transcript");
        if (auth.Outbound.HostedRequestCount != 0)
            return Fail(summaryPath, runId, "hosted calls must be zero");
        return Ok(summaryPath, new { ok = true, scenario = "jev-local-only", runId });
    }

    private static async Task<int> OutageRecovery(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        string sessionId;
        using (var diagnostics = Diag(root, runId + "-a"))
        {
            using var runtime = CaseRuntime.Open(root, clock, new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind()), diagnostics);
            runtime.StartListening();
            for (var i = 0; i < 40; i++)
                runtime.IngestSegment($"queued {i}", clock.UtcNow.AddSeconds(i * 3));
            sessionId = runtime.Intake.LoadState().SessionId!;
            if (runtime.Listening.PendingThroughOutage(sessionId).Count < 40)
                return Fail(summaryPath, runId, "expected durable pending windows");
            runtime.SuspendAll();
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        using (var diagnostics = Diag(root, runId + "-b"))
        {
            using var runtime = CaseRuntime.Open(root, clock, new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind()), diagnostics);
            if (runtime.GetListeningCase() is null)
                return Fail(summaryPath, runId, "listening case missing after restart");
            if (runtime.Intake.LoadAllSegments(runtime.GetListeningCase()!.Id).Count < 40)
                return Fail(summaryPath, runId, "segment backlog lost");
            await Task.CompletedTask;
        }
        return Ok(summaryPath, new { ok = true, scenario = "jev-outage-recovery", runId, sessionId });
    }

    private static async Task<int> Concurrency(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        using var diagnostics = Diag(root, runId);
        var mind = new OriginRoutingMind(new ListeningScriptedMind(), new SimpleDirectMind());
        using var runtime = CaseRuntime.Open(root, clock, mind, diagnostics);
        var direct = runtime.StartDirectCase("Concurrent ping");
        runtime.StartListening();
        runtime.IngestSegment("We should schedule a review.", clock.UtcNow);
        var listenId = runtime.GetListeningCase()!.Id;
        await runtime.RunUntilIdleAsync(listenId);
        var answered = await runtime.RunUntilIdleAsync(direct.Id);
        if (answered.Status != CaseStatus.Completed)
            return Fail(summaryPath, runId, "direct case should complete");
        if (runtime.GetCase(listenId)!.Status is CaseStatus.Completed or CaseStatus.Cancelled)
            return Fail(summaryPath, runId, "listening should stay alive");
        return Ok(summaryPath, new { ok = true, scenario = "jev-concurrency", runId });
    }

    private static async Task<int> CancelStale(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        using var diagnostics = Diag(root, runId);
        using var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics, () => { });
        var started = runtime.StartDirectCase("cancel me");
        await runtime.StepCaseAsync(started.Id);
        var pending = runtime.GetPendingApproval(started.Id);
        if (pending is null) return Fail(summaryPath, runId, "expected pending approval");
        runtime.CancelCase(started.Id, "user cancel");
        // Approve is not required — dispatch of not-yet-started ops after cancel must refuse execute.
        var after = runtime.DispatchOperation(pending.OperationId);
        if (after.Status is OperationStatus.Executing or OperationStatus.Completed)
            return Fail(summaryPath, runId, "dispatch after cancel must not execute");
        if (runtime.GetCase(started.Id)!.Status != CaseStatus.Cancelled)
            return Fail(summaryPath, runId, "case must remain cancelled");
        return Ok(summaryPath, new { ok = true, scenario = "jev-cancel-stale", runId });
    }

    private static async Task<int> ResearchEvidence(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        var workflows = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "workflows", "v1"));
        if (!Directory.Exists(workflows))
            workflows = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "workflows", "v1"));
        var def = ProcessCatalog.Load(Path.Combine(workflows, "research_claim.json"));
        var engine = new ProcessEngine(root, clock);
        if (!engine.Validate(def).Ok) return Fail(summaryPath, runId, "research_claim invalid");
        var run = engine.Start(def, "case-research");
        while (run.Status == "active" && run.CurrentNodeId is not null)
        {
            var node = def.Nodes.First(n => n.Id == run.CurrentNodeId);
            if (node.Type == ProcessNodeTypes.Complete) break;
            run = engine.Advance(def, run);
        }
        const string source = "Lightshift operates 20 sites.";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
        var excerpt = new ResearchClaimPolicy.ExcerptRef("a1", hash, source, 0, source.Length);
        if (!ResearchClaimPolicy.ValidateExcerpt(excerpt, source, out _))
            return Fail(summaryPath, runId, "excerpt validation failed");
        if (ResearchClaimPolicy.CitationsOnly(["a1", "a2"], ["a1"]).Count != 1)
            return Fail(summaryPath, runId, "must not auto-cite all inputs");
        await Task.CompletedTask;
        return Ok(summaryPath, new { ok = true, scenario = "jev-research-evidence", runId, runIdProcess = run.RunId });
    }

    private static int Retention(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        var evidence = new EvidenceStore(root, clock);
        var objects = new ObjectStore(root, clock);
        var retention = new RetentionService(root, evidence, objects, clock);
        var a = evidence.PutText("secret talk", sessionIds: ["sess-r"], kind: "transcript");
        var audit = retention.DeleteSession("sess-r", immediate: true);
        if (evidence.TryLoad(a.ArtifactId) is not null)
            return Fail(summaryPath, runId, "transcript should be deleted");
        if (JsonSerializer.Serialize(audit).Contains("secret talk", StringComparison.Ordinal))
            return Fail(summaryPath, runId, "audit must not contain body");
        return Ok(summaryPath, new { ok = true, scenario = "jev-retention", runId, auditId = audit.AuditId });
    }

    private static int CapabilityGrowth(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        var store = new CapabilityBundleStore(root, clock);
        var activation = new CapabilityActivation(store, clock);
        var eval = new CapabilityEvalResult { EvalId = "e1", Passed = true, ReportHash = "rep", At = clock.UtcNow };
        var bundle = store.Create("demo", "1.0.0", tools: ["t1"], permissions: ["read"], evals: [eval]);
        var act = activation.Activate(bundle, "rep");
        if (act is null) return Fail(summaryPath, runId, "activation failed");
        bundle.Permissions.Add("write");
        if (activation.IsApprovalValid(bundle, act))
            return Fail(summaryPath, runId, "permission change must invalidate approval");
        return Ok(summaryPath, new { ok = true, scenario = "jev-capability-growth", runId, bundleId = bundle.BundleId });
    }

    private static async Task<int> ProjectionRebuild(string dataRootPath, string runId)
    {
        var (root, clock, summaryPath) = Prep(dataRootPath, runId);
        using var diagnostics = Diag(root, runId);
        using (var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics))
        {
            var c = runtime.StartDirectCase("projection rebuild");
            await runtime.StepCaseAsync(c.Id);
            runtime.SuspendAll();
        }
        clock.Advance(TimeSpan.FromSeconds(5));
        using (var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics))
        {
            var ids = runtime.Cases.ListCaseIds();
            if (ids.Count == 0) return Fail(summaryPath, runId, "cases missing after reopen");
            var feed = runtime.Projections.ListFeedItems();
            if (feed.Count == 0) return Fail(summaryPath, runId, "feed projection empty after rebuild");
        }
        return Ok(summaryPath, new { ok = true, scenario = "jev-projection-rebuild", runId });
    }

    private static (DataRoot root, HarnessClock clock, string summaryPath) Prep(string dataRootPath, string runId)
    {
        var root = new DataRoot(dataRootPath);
        var clock = new HarnessClock(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        root.EnsureLayout(clock);
        var runDir = Path.Combine(root.DevRunsDirectory, runId);
        Directory.CreateDirectory(runDir);
        return (root, clock, Path.Combine(runDir, "summary.json"));
    }

    private static RuntimeDiagnostics Diag(DataRoot root, string runId)
        => new(Path.Combine(root.DevRunsDirectory, runId, "runtime.jsonl"), runId);

    private static int Ok(string summaryPath, object summary)
    {
        AtomicFile.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, RelayJson.Indented));
        Console.WriteLine(JsonSerializer.Serialize(summary, RelayJson.Compact));
        return 0;
    }

    private static int Fail(string summaryPath, string runId, string error)
    {
        var summary = new { ok = false, runId, error };
        AtomicFile.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, RelayJson.Indented));
        Console.Error.WriteLine(error);
        return 1;
    }

    private sealed class HarnessClock : IClock
    {
        public HarnessClock(DateTimeOffset start) => UtcNow = start;
        public DateTimeOffset UtcNow { get; set; }
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
