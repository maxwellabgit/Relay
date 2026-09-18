using Relay.Core.Telemetry;

namespace Relay.Core.Tests;

public sealed class ProblemDetectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-problems-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Capture_disconnected_when_transcript_changes_without_persist()
    {
        using var detector = new ProblemDetector(_dir, "run-cap");
        var t0 = DateTimeOffset.Parse("2026-09-18T15:00:00Z");
        detector.Observe(Evt(1, ProductEventNames.TranscriptChanged, t0));
        detector.Observe(Evt(2, ProductEventNames.RuntimeHeartbeat, t0.AddSeconds(2.1)));

        var open = detector.OpenProblems;
        Assert.Contains(open, p => p.Signature.StartsWith("capture_disconnected", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_dir, "problems.jsonl")));
        var md = File.ReadAllText(Path.Combine(_dir, "latest-problems.md"));
        Assert.Contains("capture_disconnected", md, StringComparison.Ordinal);
        Assert.Contains("eventSequence:", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Approved_not_executed_within_two_seconds()
    {
        using var detector = new ProblemDetector(_dir, "run-appr");
        var t0 = DateTimeOffset.Parse("2026-09-18T15:00:00Z");
        detector.Observe(Evt(1, ProductEventNames.OperationApproved, t0, operationId: "op-1"));
        detector.Observe(Evt(2, ProductEventNames.RuntimeHeartbeat, t0.AddSeconds(2.1)));

        Assert.Contains(detector.OpenProblems, p => p.Signature.StartsWith("approved_not_executed", StringComparison.Ordinal));
    }

    [Fact]
    public void Restart_duplicate_on_repeated_idempotency_key()
    {
        using var detector = new ProblemDetector(_dir, "run-dup");
        var t0 = DateTimeOffset.Parse("2026-09-18T15:00:00Z");
        detector.Observe(Evt(1, ProductEventNames.OperationCompleted, t0, operationId: "op-1", props: new()
        {
            ["idempotencyKey"] = "idem-1",
        }));
        detector.Observe(Evt(2, ProductEventNames.OperationCompleted, t0.AddSeconds(1), operationId: "op-2", props: new()
        {
            ["idempotencyKey"] = "idem-1",
        }));

        Assert.Contains(detector.OpenProblems, p => p.Signature.StartsWith("restart_duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Latest_problems_lists_only_open_problems()
    {
        using var detector = new ProblemDetector(_dir, "run-md");
        Assert.Contains("No open problems.", File.ReadAllText(detector.LatestProblemsPath), StringComparison.Ordinal);
        detector.Observe(Evt(1, ProductEventNames.CapabilityFailed, DateTimeOffset.UtcNow, errorCode: "boom"));
        var md = File.ReadAllText(detector.LatestProblemsPath);
        Assert.Contains("capability_failed", md, StringComparison.Ordinal);
        Assert.Contains("replayCommand:", md, StringComparison.Ordinal);
        Assert.DoesNotContain("ask a model", md, StringComparison.OrdinalIgnoreCase);
    }

    private static ProductEvent Evt(
        long seq,
        string name,
        DateTimeOffset ts,
        string? operationId = null,
        string? errorCode = null,
        Dictionary<string, string>? props = null) => new()
    {
        Timestamp = ts,
        MonotonicMs = seq * 10,
        RunId = "run",
        Sequence = seq,
        EventName = name,
        Level = ProductEventLevels.Info,
        OperationId = operationId,
        ErrorCode = errorCode,
        Properties = props ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };
}
