using System.Text;
using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Search;
using Relay.Core.Storage;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// §4 persistence / async outbox gates. Uses barriers and fake clocks — no long sleeps.
/// </summary>
public class AsyncPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 16, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    private RuntimeDiagnostics Diag(string name) => new(
        Path.Combine(_tmp.Root.DevRunsDirectory, name, "runtime.jsonl"), name);

    [Fact]
    public async Task Stalled_jev_does_not_block_transcript_or_other_case()
    {
        _tmp.Root.EnsureLayout(_clock);
        var jevEntered = new ManualResetEventSlim(false);
        var jevRelease = new ManualResetEventSlim(false);

        using var diagnostics = Diag("s4-jev-stall");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        runtime.SetJudgmentStallHook(async (_, ct) =>
        {
            jevEntered.Set();
            while (!jevRelease.IsSet)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        });

        var stalled = runtime.StartDirectCase("case with stalled jev");
        var cmd = runtime.EnqueueCommand(new RuntimeCommand
        {
            CommandId = "jev-stall-1",
            CaseId = stalled.Id,
            Kind = RuntimeCommandKinds.RequestJudgments,
            CreatedAt = _clock.UtcNow,
            ObjectiveRevision = 0,
            LogicalKey = "jev:stall:1",
            Payload = new Dictionary<string, JsonElement> { ["delayBarrier"] = JsonSerializer.SerializeToElement(true) },
        });

        var dispatchTask = Task.Run(() => runtime.DispatchPendingCommandsAsync(stalled.Id));
        Assert.True(jevEntered.Wait(TimeSpan.FromSeconds(5)));

        // While Jev is stalled, ingest transcript and advance another case.
        runtime.StartListening();
        var seg = runtime.IngestSegment("hello while jev stalled");
        Assert.False(string.IsNullOrEmpty(seg.SegmentId));

        var other = runtime.StartDirectCase("other case advances");
        var otherStep = await runtime.StepCaseAsync(other.Id);
        Assert.Equal(CaseStatus.Waiting, otherStep.Status);

        jevRelease.Set();
        await dispatchTask;
        Assert.Equal(RuntimeCommandStatus.Completed, runtime.Commands.TryLoad(cmd.CommandId)!.Status);
    }

    [Fact]
    public async Task Stalled_research_does_not_hold_inference_lease()
    {
        _tmp.Root.EnsureLayout(_clock);
        var researchEntered = new ManualResetEventSlim(false);
        var researchRelease = new ManualResetEventSlim(false);
        var research = new ResearchServices
        {
            Search = new FakeSearchClient().Reply(
                new SearchHitResult("X", "https://example.test/x", "snippet")),
            Fetch = new FakePageFetch().Page("https://example.test/x", "body"),
            Delegate = new FakeDelegateClient(),
        };

        using var diagnostics = Diag("s4-research-stall");
        using var runtime = CaseRuntime.Open(
            _tmp.Root, _clock, new LightshiftResearchMind(), diagnostics, research: research);
        runtime.SetResearchStallHook(async (_, ct) =>
        {
            researchEntered.Set();
            while (!researchRelease.IsSet)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        });

        var started = runtime.StartDirectCase(LightshiftResearchMind.Question, CaseKind.Research);
        await runtime.RunUntilIdleAsync(started.Id);
        var searchOp = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(searchOp.OperationId, searchOp.CanonicalHash(), runtime.GetCase(started.Id)!.Version);

        var dispatchTask = Task.Run(() => runtime.DispatchOperation(searchOp.OperationId));
        Assert.True(researchEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(runtime.Inference.TryAcquire());
        runtime.Inference.Release();

        researchRelease.Set();
        await dispatchTask;
    }

    [Fact]
    public async Task Duplicate_completion_one_transition_one_effect()
    {
        _tmp.Root.EnsureLayout(_clock);
        var effects = 0;
        using var diagnostics = Diag("s4-dup");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => effects++);

        var started = runtime.StartDirectCase("dup complete");
        await runtime.StepCaseAsync(started.Id);
        var op = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(op.OperationId, op.CanonicalHash(), runtime.GetCase(started.Id)!.Version);
        runtime.DispatchOperation(op.OperationId);
        Assert.Equal(1, effects);

        var before = runtime.Cases.LoadEvents(started.Id).Count;
        runtime.CompleteOperation(op.OperationId, new { again = true });
        runtime.DispatchOperation(op.OperationId);
        Assert.Equal(1, effects);
        Assert.Equal(1, runtime.GetOperation(op.OperationId)!.SideEffectCount);
        Assert.Contains(runtime.Cases.LoadEvents(started.Id), e => e.Type == CaseEventTypes.DuplicateIgnored);
        Assert.True(runtime.Cases.LoadEvents(started.Id).Count >= before);
    }

    [Fact]
    public async Task Cancel_before_dispatch_zero_executor_calls()
    {
        _tmp.Root.EnsureLayout(_clock);
        var effects = 0;
        using var diagnostics = Diag("s4-cancel-before");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => effects++);

        var started = runtime.StartDirectCase("cancel before dispatch");
        await runtime.StepCaseAsync(started.Id);
        var op = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(op.OperationId, op.CanonicalHash(), runtime.GetCase(started.Id)!.Version);
        runtime.CancelCase(started.Id);

        var result = runtime.DispatchOperation(op.OperationId);
        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task Cancel_during_dispatch_late_result_does_not_reopen()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-cancel-during");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => { });

        var started = runtime.StartDirectCase("cancel during");
        await runtime.StepCaseAsync(started.Id);
        var op = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(op.OperationId, op.CanonicalHash(), runtime.GetCase(started.Id)!.Version);

        // Mark executing (dispatch started) then cancel the case.
        var env = runtime.GetOperation(op.OperationId)!;
        env.Status = OperationStatus.Executing;
        runtime.Operations.Save(env);
        runtime.CancelCase(started.Id);
        Assert.Equal(CaseStatus.Cancelled, runtime.GetCase(started.Id)!.Status);
        Assert.Equal(OperationStatus.Executing, runtime.GetOperation(op.OperationId)!.Status);

        var accepted = runtime.AcceptOperationResult(op.OperationId, new OperationApplyResult(true, "late", "op-result:" + op.OperationId));
        Assert.Equal(OperationStatus.Completed, accepted.Status);
        Assert.Equal(CaseStatus.Cancelled, runtime.GetCase(started.Id)!.Status);
    }

    [Fact]
    public async Task Objective_revision_invalidates_older_result()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-objrev");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => { });

        var started = runtime.StartDirectCase("objective rev");
        await runtime.StepCaseAsync(started.Id);
        var op = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(op.OperationId, op.CanonicalHash(), runtime.GetCase(started.Id)!.Version);

        // Bump objective revision above the envelope's caseVersion binding.
        runtime.ReviseObjective(started.Id, "revised objective");
        var record = runtime.GetCase(started.Id)!;
        Assert.True(record.ObjectiveRevision > 0);

        // Force envelope caseVersion below objective revision.
        var env = runtime.GetOperation(op.OperationId)!;
        env.CaseVersion = 0;
        env.Status = OperationStatus.Executing;
        runtime.Operations.Save(env);

        var accepted = runtime.AcceptOperationResult(op.OperationId, new OperationApplyResult(true, "stale", null));
        Assert.Equal(OperationStatus.Failed, accepted.Status);
        Assert.Contains(runtime.Cases.LoadEvents(started.Id),
            e => e.Type == CaseDomainEventTypes.DuplicateIgnored
                 || (e.Payload.ValueKind == JsonValueKind.Object
                     && e.Payload.TryGetProperty("reason", out var r)
                     && r.GetString() == "objective_revision_stale"));
    }

    [Fact]
    public void Unrelated_segment_does_not_invalidate_decision()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-segment");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);

        var started = runtime.StartDirectCase("deps");
        runtime.SetDecisionDependencies(started.Id, ["segment:relevant-1", "artifact:abc"]);
        Assert.True(runtime.SegmentInvalidatesDecision(started.Id, "relevant-1"));
        Assert.False(runtime.SegmentInvalidatesDecision(started.Id, "unrelated-9"));
    }

    [Fact]
    public async Task Crash_after_transition_before_dispatch_resumes_once()
    {
        _tmp.Root.EnsureLayout(_clock);
        string caseId;
        string commandId;
        var effects = 0;

        {
            using var diagnostics = Diag("s4-crash-a");
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => effects++);
            var started = runtime.StartDirectCase("crash resume");
            caseId = started.Id;
            // Persist a pending command without dispatching (simulates crash after transition).
            commandId = "resume-cmd-1";
            runtime.EnqueueCommand(new RuntimeCommand
            {
                CommandId = commandId,
                CaseId = caseId,
                Kind = RuntimeCommandKinds.PublishFeedItem,
                CreatedAt = _clock.UtcNow,
                LogicalKey = "feed:crash:" + caseId,
                Payload = new Dictionary<string, JsonElement>
                {
                    ["text"] = JsonSerializer.SerializeToElement("resumed feed"),
                    ["level"] = JsonSerializer.SerializeToElement("ambient"),
                },
            });
            // Claim then "crash" without completing.
            Assert.True(runtime.Commands.TryClaim(commandId, "dead-owner", _clock.UtcNow, out _));
        }

        {
            using var diagnostics = Diag("s4-crash-b");
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => effects++);
            var cmd = runtime.Commands.TryLoad(commandId)!;
            Assert.Equal(RuntimeCommandStatus.Pending, cmd.Status); // reconstruct reset claimed → pending
            await runtime.DispatchPendingCommandsAsync(caseId);
            Assert.Equal(RuntimeCommandStatus.Completed, runtime.Commands.TryLoad(commandId)!.Status);
            await runtime.DispatchPendingCommandsAsync(caseId);
            Assert.Equal(1, runtime.Projections.ListFeedItems(caseId).Count(f => f.Text == "resumed feed"));
        }
    }

    [Fact]
    public async Task Crash_after_local_write_before_ack_no_duplicate_write()
    {
        _tmp.Root.EnsureLayout(_clock);
        var effects = 0;
        using var diagnostics = Diag("s4-idempotent-write");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => effects++);

        var started = runtime.StartDirectCase("idempotent write");
        await runtime.StepCaseAsync(started.Id);
        var op = runtime.GetPendingApproval(started.Id)!;
        runtime.ApproveOperation(op.OperationId, op.CanonicalHash(), runtime.GetCase(started.Id)!.Version);

        // Simulate write completed (stable op-derived object) before ack.
        var stableId = "op-result:" + op.OperationId;
        runtime.Objects.PutJson(new { ok = true, operationId = op.OperationId }, stableId);
        var env = runtime.GetOperation(op.OperationId)!;
        env.Status = OperationStatus.Executing;
        runtime.Operations.Save(env);

        var accepted = runtime.AcceptOperationResult(op.OperationId, new OperationApplyResult(true, "resumed", stableId));
        Assert.Equal(OperationStatus.Completed, accepted.Status);
        Assert.Equal(stableId, accepted.ResultRef);

        // Second accept is duplicate — no extra side effect counter bump beyond first ack.
        var again = runtime.AcceptOperationResult(op.OperationId, new OperationApplyResult(true, "dup", stableId));
        Assert.Equal(OperationStatus.Completed, again.Status);
        Assert.Equal(1, runtime.GetOperation(op.OperationId)!.SideEffectCount);
    }

    [Fact]
    public async Task Delete_sqlite_projections_rebuilds_pending_work()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-rebuild");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);

        var started = runtime.StartDirectCase("rebuild projections");
        await runtime.StepCaseAsync(started.Id);
        var op = runtime.GetPendingApproval(started.Id)!;
        Assert.NotNull(op);

        // Delete SQLite file while runtime still holds the connection — rebuild in-process.
        runtime.RebuildProjections();

        var pending = runtime.GetPendingApproval(started.Id);
        Assert.NotNull(pending);
        Assert.Equal(op.OperationId, pending!.OperationId);

        // Also verify cold reopen after deleting the db file.
        var dbPath = _tmp.Root.ProjectionsDatabasePath;
        runtime.Dispose();
        File.Delete(dbPath);

        using var diagnostics2 = Diag("s4-rebuild-2");
        using var runtime2 = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics2);
        var pending2 = runtime2.GetPendingApproval(started.Id);
        Assert.NotNull(pending2);
        Assert.Equal(op.OperationId, pending2!.OperationId);
        Assert.Equal(CaseStatus.Waiting, runtime2.GetCase(started.Id)!.Status);
    }

    [Fact]
    public void Two_cases_cannot_claim_same_logical_command()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-logical-claim");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);

        var a = runtime.StartDirectCase("case a");
        var b = runtime.StartDirectCase("case b");
        const string logical = "shared-logical-work-v1";

        var cmdA = runtime.EnqueueCommand(new RuntimeCommand
        {
            CommandId = "cmd-a",
            CaseId = a.Id,
            Kind = RuntimeCommandKinds.RetrieveContext,
            LogicalKey = logical,
            CreatedAt = _clock.UtcNow,
        });
        var cmdB = runtime.EnqueueCommand(new RuntimeCommand
        {
            CommandId = "cmd-b",
            CaseId = b.Id,
            Kind = RuntimeCommandKinds.RetrieveContext,
            LogicalKey = logical,
            CreatedAt = _clock.UtcNow,
        });

        Assert.True(runtime.Dispatcher.TryClaim(cmdA.CommandId, _clock.UtcNow, out _));
        // Second case cannot acquire the same logical command for dispatch.
        Assert.False(runtime.Commands.TryClaimLogical(logical, "other-owner", _clock.UtcNow, out _));
        Assert.False(runtime.Dispatcher.TryClaim(cmdB.CommandId, _clock.UtcNow, out _));
    }

    [Fact]
    public async Task Jsonl_truncated_tail_recovers_prefix_with_incident()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-jsonl");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        var started = runtime.StartDirectCase("jsonl recovery");
        await runtime.StepCaseAsync(started.Id);

        var path = runtime.Cases.EventsPath(started.Id);
        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.True(text.EndsWith('\n'));
        // Append a torn final line (no trailing newline) after valid records.
        File.WriteAllText(path, text + "{\"eventId\":\"torn", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var recovery = runtime.Cases.LoadEventsWithRecovery(started.Id);
        Assert.True(recovery.TruncatedTailRecovered);
        Assert.False(recovery.MidFileCorruptionStopped);
        Assert.NotNull(recovery.IncidentPath);
        Assert.True(File.Exists(recovery.IncidentPath));
        Assert.NotEmpty(recovery.Events);
    }

    [Fact]
    public async Task Jsonl_mid_file_corruption_stops_recovery()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("s4-jsonl-mid");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        var started = runtime.StartDirectCase("jsonl mid");
        await runtime.StepCaseAsync(started.Id);

        var path = runtime.Cases.EventsPath(started.Id);
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        Assert.True(lines.Count >= 2);
        lines[1] = "{not-json";
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var recovery = runtime.Cases.LoadEventsWithRecovery(started.Id);
        Assert.True(recovery.MidFileCorruptionStopped);
        Assert.False(recovery.TruncatedTailRecovered);
        Assert.Single(recovery.Events); // only the first valid line
        Assert.NotNull(recovery.IncidentPath);
    }
}
