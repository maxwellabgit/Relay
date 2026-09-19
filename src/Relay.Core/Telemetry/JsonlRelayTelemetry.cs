using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Telemetry;

/// <summary>
/// Append-only JSONL telemetry sink at <c>{runDir}\events.jsonl</c>.
/// Uses <see cref="FileShare.Read"/>, an internal lock, one JSON object per line,
/// and flushes after error / approval / operation / judgment / user-feedback events.
/// </summary>
public sealed class JsonlRelayTelemetry : IRelayTelemetry
{
    private static readonly HashSet<string> FlushEventNames = new(StringComparer.Ordinal)
    {
        ProductEventNames.AppCrashed,
        ProductEventNames.UiCommandFailed,
        ProductEventNames.CaseFailed,
        ProductEventNames.JudgmentAuthorized,
        ProductEventNames.JudgmentBlocked,
        ProductEventNames.JudgmentDispatched,
        ProductEventNames.JudgmentCompleted,
        ProductEventNames.JudgmentDeferred,
        ProductEventNames.OperationProposed,
        ProductEventNames.OperationApproved,
        ProductEventNames.OperationExecuting,
        ProductEventNames.OperationCompleted,
        ProductEventNames.OperationFailed,
        ProductEventNames.CapabilityFailed,
        ProductEventNames.ProblemReported,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string? _appVersion;
    private readonly Stopwatch _mono = Stopwatch.StartNew();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private FileStream? _stream;
    private long _seq;
    private bool _disposed;

    public JsonlRelayTelemetry(string runDir, string runId, string? appVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        RunDir = runDir;
        RunId = runId;
        _appVersion = appVersion;
        Directory.CreateDirectory(runDir);
        _path = Path.Combine(runDir, "events.jsonl");
    }

    public string RunId { get; }
    public string? RunDir { get; }
    public string EventsPath => _path;
    public long LastSequence
    {
        get { lock (_gate) return _seq; }
    }

    public ProductEvent Emit(ProductEventDraft draft)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.EventName);

        var props = TelemetryRedactor.Allow(draft.EventName, draft.Properties);
        ProductEvent evt;
        lock (_gate)
        {
            _seq++;
            evt = new ProductEvent
            {
                SchemaVersion = 1,
                Timestamp = _startedAt + _mono.Elapsed,
                MonotonicMs = _mono.ElapsedMilliseconds,
                RunId = RunId,
                Sequence = _seq,
                AppVersion = _appVersion,
                EventName = draft.EventName,
                Level = string.IsNullOrWhiteSpace(draft.Level) ? ProductEventLevels.Info : draft.Level,
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

            var line = JsonSerializer.Serialize(evt, RelayJson.Compact) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            var stream = EnsureStream();
            stream.Write(bytes, 0, bytes.Length);
            if (ShouldFlush(evt))
                stream.Flush(flushToDisk: true);
        }

        return evt;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream?.Flush(flushToDisk: true); } catch { /* best effort */ }
            _stream?.Dispose();
            _stream = null;
        }
    }

    private FileStream EnsureStream()
    {
        if (_stream is not null) return _stream;
        _stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return _stream;
    }

    private static bool ShouldFlush(ProductEvent evt)
    {
        if (string.Equals(evt.Level, ProductEventLevels.Error, StringComparison.Ordinal))
            return true;
        return FlushEventNames.Contains(evt.EventName);
    }
}
