namespace Relay.Core.Telemetry;

public interface IRelayTelemetry : IDisposable
{
    string RunId { get; }
    string? RunDir { get; }
    long LastSequence { get; }

    ProductEvent Emit(ProductEventDraft draft);
}

/// <summary>Mutable builder for a single emit. Properties must be scalars after redaction.</summary>
public sealed class ProductEventDraft
{
    public required string EventName { get; init; }
    public string Level { get; init; } = ProductEventLevels.Info;
    public string? SessionId { get; init; }
    public string? CaseId { get; init; }
    public long? CaseVersion { get; init; }
    public string? ProjectId { get; init; }
    public string? CapabilityId { get; init; }
    public string? OperationId { get; init; }
    public string? JudgmentId { get; init; }
    public string? Phase { get; init; }
    public string? Outcome { get; init; }
    public long? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? PayloadRef { get; init; }
    public Dictionary<string, string>? Properties { get; init; }
}

public sealed class NullRelayTelemetry : IRelayTelemetry
{
    private long _seq;

    public NullRelayTelemetry(string runId = "null") => RunId = runId;

    public string RunId { get; }
    public string? RunDir => null;
    public long LastSequence => Volatile.Read(ref _seq);

    public ProductEvent Emit(ProductEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var seq = Interlocked.Increment(ref _seq);
        var props = TelemetryRedactor.Allow(draft.EventName, draft.Properties);
        return new ProductEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            MonotonicMs = Environment.TickCount64,
            RunId = RunId,
            Sequence = seq,
            EventName = draft.EventName,
            Level = draft.Level,
            SessionId = draft.SessionId,
            CaseId = draft.CaseId,
            CaseVersion = draft.CaseVersion,
            ProjectId = draft.ProjectId,
            CapabilityId = draft.CapabilityId,
            OperationId = draft.OperationId,
            JudgmentId = draft.JudgmentId,
            Phase = draft.Phase,
            Outcome = draft.Outcome,
            DurationMs = draft.DurationMs,
            ErrorCode = draft.ErrorCode,
            PayloadRef = draft.PayloadRef,
            Properties = props,
        };
    }

    public void Dispose() { }
}
