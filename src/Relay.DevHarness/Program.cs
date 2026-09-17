using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.DevHarness;

/// <summary>
/// Minimal Slice 1 console harness: runs the scripted propose → suspend → restart → approve →
/// execute → duplicate-completion scenario and writes diagnostics under .dev-runs/{run-id}/.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? dataRootPath = null;
        string runId = "slice1-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-root" && i + 1 < args.Length) dataRootPath = args[++i];
            else if (args[i] == "--run-id" && i + 1 < args.Length) runId = args[++i];
        }

        if (string.IsNullOrWhiteSpace(dataRootPath))
        {
            Console.Error.WriteLine("Usage: Relay.DevHarness --data-root <path> [--run-id <id>]");
            return 2;
        }

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
            // Pass 1: start + propose + suspend
            using (var runtime = CaseRuntime.Open(root, clock, new ScriptedCaseMind(), diagnostics, () => sideEffects++))
            {
                var started = runtime.StartDirectCase("dev harness: slice1 side effect");
                caseId = started.Id;
                var stepped = await runtime.StepNextAsync();
                if (stepped is null || stepped.Status != CaseStatus.Waiting)
                {
                    return Fail(summaryPath, runId, "expected waiting case after propose", sideEffects);
                }

                var pending = runtime.GetPendingApproval(caseId)
                    ?? throw new InvalidOperationException("missing pending approval");
                operationId = pending.OperationId;
                envelopeHash = pending.CanonicalHash();
                runtime.SuspendAll();
            }

            clock.Advance(TimeSpan.FromMinutes(1));

            // Pass 2: restart + approve + execute + duplicate complete
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

                var summary = new
                {
                    ok = true,
                    runId,
                    caseId,
                    operationId,
                    sideEffects,
                    diagnostics = diagnosticsPath,
                };
                AtomicFile.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, RelayJson.Indented));
                Console.WriteLine(JsonSerializer.Serialize(summary, RelayJson.Compact));
                return 0;
            }
        }
    }

    private static int Fail(string summaryPath, string runId, string error, int sideEffects)
    {
        var summary = new { ok = false, runId, error, sideEffects };
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
