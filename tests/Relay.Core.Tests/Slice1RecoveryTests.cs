using Relay.Core.Cases;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Slice 1 exit test: start → propose → suspend → restart → approve → execute once →
/// duplicate completion must not repeat the side effect.
/// </summary>
public class Slice1RecoveryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);
    private readonly List<string> _diagPaths = [];

    [Fact]
    public async Task Restart_Through_Approval_Is_Idempotent()
    {
        var sideEffects = 0;
        string caseId;
        string operationId;
        string envelopeHash;
        long caseVersionAtPropose;

        // 1–2. Start direct task and produce an approval (scripted mind proposes).
        {
            using var diagnostics = NewDiagnostics("run-a");
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => sideEffects++);

            var started = runtime.StartDirectCase("slice1 exit: run the counter once");
            caseId = started.Id;

            var stepped = await runtime.StepNextAsync();
            Assert.NotNull(stepped);
            Assert.Equal(CaseStatus.Waiting, stepped!.Status);

            var pending = runtime.GetPendingApproval(caseId);
            Assert.NotNull(pending);
            Assert.Equal(OperationStatus.AwaitingApproval, pending!.Status);
            operationId = pending.OperationId;
            envelopeHash = pending.CanonicalHash();
            caseVersionAtPropose = stepped.Version;
            Assert.Equal(envelopeHash, pending.CanonicalHashValue);

            // 3. Close / suspend cleanly.
            runtime.SuspendAll();
            var suspended = runtime.GetCase(caseId);
            Assert.NotNull(suspended);
            Assert.Equal(CaseStatus.Suspended, suspended!.Status);
        }

        Assert.Equal(0, sideEffects);

        // 4. Restart (new CaseRuntime on same data root).
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            using var diagnostics = NewDiagnostics("run-b");
            using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => sideEffects++);

            var resumed = runtime.GetCase(caseId);
            Assert.NotNull(resumed);
            Assert.Equal(CaseStatus.Waiting, resumed!.Status);

            var pending = runtime.GetPendingApproval(caseId);
            Assert.NotNull(pending);
            Assert.Equal(operationId, pending!.OperationId);
            Assert.Equal(envelopeHash, pending.CanonicalHash());

            // 5. Approve it (case version check uses current record version).
            var approved = runtime.ApproveOperation(operationId, envelopeHash, resumed.Version);
            Assert.Equal(OperationStatus.Approved, approved.Status);
            Assert.False(string.IsNullOrEmpty(approved.ApprovalId));

            // 6. Execute once.
            var executed = runtime.ExecuteOperation(operationId);
            Assert.Equal(OperationStatus.Completed, executed.Status);
            Assert.Equal(1, executed.SideEffectCount);
            Assert.Equal(1, sideEffects);

            // 7. Inject duplicate completion.
            var again = runtime.CompleteOperation(operationId, new { duplicate = true });
            Assert.Equal(OperationStatus.Completed, again.Status);

            // Execute again must also be a no-op.
            var reexec = runtime.ExecuteOperation(operationId);
            Assert.Equal(OperationStatus.Completed, reexec.Status);

            // 8. Confirm no repeated side effect.
            Assert.Equal(1, sideEffects);
            Assert.Equal(1, runtime.GetOperation(operationId)!.SideEffectCount);

            // Events were persisted (including duplicate_ignored).
            var events = runtime.Cases.LoadEvents(caseId);
            Assert.Contains(events, e => e.Type == CaseEventTypes.OperationProposed);
            Assert.Contains(events, e => e.Type == CaseEventTypes.OperationApproved);
            Assert.Contains(events, e => e.Type == CaseEventTypes.OperationExecuted);
            Assert.Contains(events, e => e.Type == CaseEventTypes.DuplicateIgnored);

            // Ready queue / layout dirs exist.
            Assert.True(Directory.Exists(_tmp.Root.CasesDirectory));
            Assert.True(Directory.Exists(_tmp.Root.ObjectsDirectory));
            Assert.True(Directory.Exists(_tmp.Root.OperationsDirectory));
            Assert.True(Directory.Exists(_tmp.Root.ProjectionsDirectory));
            Assert.True(File.Exists(runtime.Cases.RecordPath(caseId)));
            Assert.True(File.Exists(runtime.Cases.EventsPath(caseId)));
            Assert.True(File.Exists(runtime.Operations.OperationPath(operationId)));
        }

        // caseVersionAtPropose is captured for clarity; approve uses resumed version after suspend events.
        Assert.True(caseVersionAtPropose > 0);
    }

    [Fact]
    public void CanonicalHash_Changes_When_Arguments_Change()
    {
        var a = new OperationEnvelope
        {
            OperationId = "01A",
            CaseId = "01C",
            CaseVersion = 1,
            Capability = "x",
            IdempotencyKey = "k1",
            Arguments = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["n"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
            },
        };
        var b = new OperationEnvelope
        {
            OperationId = "01A",
            CaseId = "01C",
            CaseVersion = 1,
            Capability = "x",
            IdempotencyKey = "k1",
            Arguments = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["n"] = System.Text.Json.JsonSerializer.SerializeToElement(2),
            },
        };
        Assert.NotEqual(a.CanonicalHash(), b.CanonicalHash());
        a.Status = OperationStatus.Approved;
        Assert.Equal(a.CanonicalHash(), new OperationEnvelope
        {
            OperationId = "01A",
            CaseId = "01C",
            CaseVersion = 1,
            Capability = "x",
            IdempotencyKey = "k1",
            Arguments = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["n"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
            },
            Status = OperationStatus.Requested,
        }.CanonicalHash());
    }

    private RuntimeDiagnostics NewDiagnostics(string runId)
    {
        var path = Path.Combine(_tmp.Root.DevRunsDirectory, runId, "runtime.jsonl");
        _diagPaths.Add(path);
        return new RuntimeDiagnostics(path, runId);
    }

    public void Dispose() => _tmp.Dispose();
}
