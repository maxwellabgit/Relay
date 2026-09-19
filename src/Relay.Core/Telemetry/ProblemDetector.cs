using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;

namespace Relay.Core.Telemetry;

public static class ProblemSeverities
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Error = "error";
    public const string Critical = "critical";
}

public static class ProblemStatuses
{
    public const string Open = "open";
    public const string Resolved = "resolved";
}

public sealed class ProblemRecord
{
    [JsonPropertyName("problemId")] public required string ProblemId { get; init; }
    [JsonPropertyName("signature")] public required string Signature { get; init; }
    [JsonPropertyName("severity")] public required string Severity { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("firstSeenAt")] public DateTimeOffset FirstSeenAt { get; init; }
    [JsonPropertyName("lastSeenAt")] public DateTimeOffset LastSeenAt { get; init; }
    [JsonPropertyName("occurrences")] public int Occurrences { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
    [JsonPropertyName("expected")] public string? Expected { get; init; }
    [JsonPropertyName("actual")] public string? Actual { get; init; }
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("operationId")] public string? OperationId { get; init; }
    [JsonPropertyName("eventSequenceStart")] public long EventSequenceStart { get; init; }
    [JsonPropertyName("eventSequenceEnd")] public long EventSequenceEnd { get; init; }
    [JsonPropertyName("replayCommand")] public string? ReplayCommand { get; init; }
    [JsonPropertyName("userNoteRef")] public string? UserNoteRef { get; init; }
}

/// <summary>
/// Consumes product events and appends deduplicated problem state changes to problems.jsonl.
/// Regenerates latest-problems.md deterministically whenever problems.jsonl changes.
/// </summary>
public sealed class ProblemDetector : IDisposable
{
    private readonly object _gate = new();
    private readonly string _runDir;
    private readonly string _runId;
    private readonly string _problemsPath;
    private readonly string _latestPath;
    private readonly List<ProductEvent> _events = [];
    private readonly Dictionary<string, ProblemRecord> _open = new(StringComparer.Ordinal);
    private readonly List<ProblemRecord> _history = [];
    private bool _listening;
    private long? _lastTranscriptChangedSeq;
    private DateTimeOffset? _lastTranscriptChangedAt;
    private long? _lastApprovalSeq;
    private DateTimeOffset? _lastApprovalAt;
    private string? _lastApprovalOperationId;
    private readonly Dictionary<string, (long Seq, DateTimeOffset At, string? CaseId)> _pendingCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<(string Phase, string DecisionSig, long Seq)>> _caseLoops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _completedByIdempotency = new(StringComparer.Ordinal);
    private long? _queueNonEmptySinceSeq;
    private DateTimeOffset? _queueNonEmptySinceAt;
    private bool _disposed;

    public ProblemDetector(string runDir, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _runDir = runDir;
        _runId = runId;
        Directory.CreateDirectory(runDir);
        _problemsPath = Path.Combine(runDir, "problems.jsonl");
        _latestPath = Path.Combine(runDir, "latest-problems.md");
        WriteLatestMarkdown();
    }

    public string ProblemsPath => _problemsPath;
    public string LatestProblemsPath => _latestPath;
    public IReadOnlyCollection<ProblemRecord> OpenProblems
    {
        get { lock (_gate) return _open.Values.OrderBy(p => p.FirstSeenAt).ToArray(); }
    }

    public void Observe(ProductEvent evt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            _events.Add(evt);
            Detect(evt);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void Detect(ProductEvent evt)
    {
        switch (evt.EventName)
        {
            case ProductEventNames.TranscriptChanged:
                _listening = true;
                _lastTranscriptChangedSeq = evt.Sequence;
                _lastTranscriptChangedAt = evt.Timestamp;
                break;
            case ProductEventNames.TranscriptWindowPersisted:
                _lastTranscriptChangedSeq = null;
                _lastTranscriptChangedAt = null;
                break;
            case ProductEventNames.UiCommandStarted:
                _pendingCommands[CommandKey(evt)] = (evt.Sequence, evt.Timestamp, evt.CaseId);
                break;
            case ProductEventNames.UiCommandCompleted:
            case ProductEventNames.UiCommandFailed:
            case ProductEventNames.OperationApproved:
            case ProductEventNames.JudgmentDeferred:
                _pendingCommands.Remove(CommandKey(evt));
                if (evt.EventName == ProductEventNames.OperationApproved)
                {
                    _lastApprovalSeq = evt.Sequence;
                    _lastApprovalAt = evt.Timestamp;
                    _lastApprovalOperationId = evt.OperationId;
                }
                break;
            case ProductEventNames.OperationExecuting:
                if (string.Equals(evt.OperationId, _lastApprovalOperationId, StringComparison.Ordinal))
                {
                    _lastApprovalSeq = null;
                    _lastApprovalAt = null;
                    _lastApprovalOperationId = null;
                }
                break;
            case ProductEventNames.QueueEnqueued:
                if (_queueNonEmptySinceAt is null)
                {
                    _queueNonEmptySinceAt = evt.Timestamp;
                    _queueNonEmptySinceSeq = evt.Sequence;
                }
                break;
            case ProductEventNames.QueueIdle:
            case ProductEventNames.QueueDequeued:
                if (evt.EventName == ProductEventNames.QueueIdle ||
                    (evt.Properties.TryGetValue("readyCount", out var rc) && rc == "0"))
                {
                    _queueNonEmptySinceAt = null;
                    _queueNonEmptySinceSeq = null;
                }
                break;
            case ProductEventNames.CasePhaseChanged:
                TrackCaseLoop(evt);
                break;
            case ProductEventNames.CapabilityFailed:
                Raise(
                    "capability_failed",
                    ProblemSeverities.Error,
                    "A capability handler failed.",
                    expected: "capability.completed",
                    actual: evt.ErrorCode ?? "capability.failed",
                    evt,
                    evt.CaseId,
                    evt.OperationId);
                break;
            case ProductEventNames.OperationFailed:
                Raise(
                    "operation_failed",
                    ProblemSeverities.Error,
                    "An operation failed during execution.",
                    expected: "operation.completed",
                    actual: evt.ErrorCode ?? "operation.failed",
                    evt,
                    evt.CaseId,
                    evt.OperationId);
                break;
            case ProductEventNames.AppCrashed:
                Raise(
                    "unhandled_exception",
                    ProblemSeverities.Critical,
                    "The application crashed.",
                    expected: "clean shutdown",
                    actual: evt.ErrorCode ?? "app.crashed",
                    evt,
                    evt.CaseId,
                    evt.OperationId);
                break;
            case ProductEventNames.OperationCompleted:
                TrackIdempotency(evt);
                break;
            case ProductEventNames.JudgmentBlocked:
                if (evt.Properties.TryGetValue("hadGrant", out var had) && had == "true")
                {
                    Raise(
                        "hosted_grant_mismatch",
                        ProblemSeverities.Error,
                        "A grant was present but judgment was not authorized.",
                        expected: "judgment.authorized",
                        actual: "judgment.blocked",
                        evt,
                        evt.CaseId,
                        evt.OperationId);
                }
                break;
            case ProductEventNames.ProblemReported:
                Raise(
                    "user_reported:" + (evt.PayloadRef ?? evt.Sequence.ToString(CultureInfo.InvariantCulture)),
                    evt.Properties.GetValueOrDefault("severity") ?? ProblemSeverities.Error,
                    "User reported a problem.",
                    expected: "see protected problem object",
                    actual: "user report",
                    evt,
                    evt.CaseId,
                    evt.OperationId);
                if (_open.TryGetValue("user_reported:" + (evt.PayloadRef ?? evt.Sequence.ToString(CultureInfo.InvariantCulture)), out var reported) &&
                    evt.PayloadRef is not null)
                {
                    _open[reported.Signature] = new ProblemRecord
                    {
                        ProblemId = reported.ProblemId,
                        Signature = reported.Signature,
                        Severity = reported.Severity,
                        Status = reported.Status,
                        FirstSeenAt = reported.FirstSeenAt,
                        LastSeenAt = reported.LastSeenAt,
                        Occurrences = reported.Occurrences,
                        Summary = reported.Summary,
                        Expected = reported.Expected,
                        Actual = reported.Actual,
                        CaseId = reported.CaseId,
                        OperationId = reported.OperationId,
                        EventSequenceStart = reported.EventSequenceStart,
                        EventSequenceEnd = reported.EventSequenceEnd,
                        ReplayCommand = reported.ReplayCommand,
                        UserNoteRef = evt.PayloadRef,
                    };
                    WriteLatestMarkdown();
                }
                break;
            case ProductEventNames.CaseCreated:
                if (evt.Properties.TryGetValue("derived", out var derived) && derived == "true" &&
                    (!evt.Properties.TryGetValue("hasSourceObject", out var hasSrc) || hasSrc != "true"))
                {
                    Raise(
                        "source_lineage_lost",
                        ProblemSeverities.Error,
                        "A derived case was created without an object-store source.",
                        expected: "source object reference on child",
                        actual: "missing source",
                        evt,
                        evt.CaseId,
                        evt.OperationId);
                }
                break;
        }

        EvaluateTimers(evt);
    }

    private void EvaluateTimers(ProductEvent evt)
    {
        if (_listening &&
            _lastTranscriptChangedAt is { } changedAt &&
            _lastTranscriptChangedSeq is { } changedSeq &&
            (evt.Timestamp - changedAt) >= TimeSpan.FromSeconds(2))
        {
            Raise(
                "capture_disconnected:" + changedSeq.ToString(CultureInfo.InvariantCulture),
                ProblemSeverities.Error,
                "Listening is on and transcript changed, but no window was persisted within 2 seconds.",
                expected: "transcript.window.persisted within 2s",
                actual: "no persisted window",
                evt,
                evt.CaseId,
                null,
                changedSeq,
                evt.Sequence);
            _lastTranscriptChangedAt = null;
        }

        foreach (var (key, pending) in _pendingCommands.ToArray())
        {
            if ((evt.Timestamp - pending.At) < TimeSpan.FromSeconds(15))
                continue;
            Raise(
                "command_stalled:" + key,
                ProblemSeverities.Warn,
                "Composer submission has no terminal result, approval, or declared wait within 15 seconds.",
                expected: "ui.command.completed / approval / wait within 15s",
                actual: "stalled",
                evt,
                pending.CaseId,
                null,
                pending.Seq,
                evt.Sequence);
            _pendingCommands.Remove(key);
        }

        if (_lastApprovalAt is { } approvedAt &&
            _lastApprovalSeq is { } approvedSeq &&
            (evt.Timestamp - approvedAt) >= TimeSpan.FromSeconds(2))
        {
            Raise(
                "approved_not_executed:" + (_lastApprovalOperationId ?? approvedSeq.ToString(CultureInfo.InvariantCulture)),
                ProblemSeverities.Error,
                "Approval has no executing event within 2 seconds.",
                expected: "operation.executing within 2s of approval",
                actual: "still approved",
                evt,
                evt.CaseId,
                _lastApprovalOperationId,
                approvedSeq,
                evt.Sequence);
            _lastApprovalAt = null;
        }

        if (_queueNonEmptySinceAt is { } since &&
            _queueNonEmptySinceSeq is { } qSeq &&
            (evt.Timestamp - since) >= TimeSpan.FromSeconds(2))
        {
            Raise(
                "active_queue_not_pumped:" + qSeq.ToString(CultureInfo.InvariantCulture),
                ProblemSeverities.Error,
                "Runnable queue remained nonempty for 2 seconds without idle.",
                expected: "queue drained or heartbeat pump",
                actual: "queue stalled",
                evt,
                evt.CaseId,
                null,
                qSeq,
                evt.Sequence);
            _queueNonEmptySinceAt = null;
        }

        if (evt.Properties.TryGetValue("stepsUsed", out var usedRaw) &&
            evt.Properties.TryGetValue("maxSteps", out var maxRaw) &&
            int.TryParse(usedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) &&
            int.TryParse(maxRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) &&
            max > 0 && used >= max)
        {
            Raise(
                "budget_exceeded:" + (evt.CaseId ?? evt.Sequence.ToString(CultureInfo.InvariantCulture)),
                ProblemSeverities.Error,
                "Case steps reached the configured maximum.",
                expected: $"stepsUsed < {max}",
                actual: $"stepsUsed={used}",
                evt,
                evt.CaseId,
                evt.OperationId);
        }
    }

    private void TrackCaseLoop(ProductEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.CaseId)) return;
        var phase = evt.Phase ?? "";
        var decisionSig = evt.Properties.GetValueOrDefault("decisionSignature") ?? evt.Outcome ?? "";
        if (!_caseLoops.TryGetValue(evt.CaseId, out var q))
        {
            q = new Queue<(string, string, long)>();
            _caseLoops[evt.CaseId] = q;
        }

        q.Enqueue((phase, decisionSig, evt.Sequence));
        while (q.Count > 3) q.Dequeue();
        if (q.Count == 3)
        {
            var items = q.ToArray();
            if (items.All(i => i.Phase == items[0].Phase && i.DecisionSig == items[0].DecisionSig) &&
                !string.IsNullOrWhiteSpace(items[0].Phase))
            {
                Raise(
                    "case_loop:" + evt.CaseId + ":" + items[0].Phase + ":" + items[0].DecisionSig,
                    ProblemSeverities.Error,
                    "The same case phase and decision signature repeated three times.",
                    expected: "progressing phase/decision",
                    actual: $"{items[0].Phase}/{items[0].DecisionSig} x3",
                    evt,
                    evt.CaseId,
                    evt.OperationId,
                    items[0].Seq,
                    evt.Sequence);
                q.Clear();
            }
        }
    }

    private void TrackIdempotency(ProductEvent evt)
    {
        if (!evt.Properties.TryGetValue("idempotencyKey", out var key) || string.IsNullOrWhiteSpace(key))
            return;
        _completedByIdempotency.TryGetValue(key, out var count);
        count++;
        _completedByIdempotency[key] = count;
        if (count >= 2)
        {
            Raise(
                "restart_duplicate:" + key,
                ProblemSeverities.Error,
                "One idempotency key produced multiple completed operations.",
                expected: "single completed operation per idempotency key",
                actual: $"completedCount={count}",
                evt,
                evt.CaseId,
                evt.OperationId);
        }
    }

    private void Raise(
        string signature,
        string severity,
        string summary,
        string expected,
        string actual,
        ProductEvent evt,
        string? caseId,
        string? operationId,
        long? seqStart = null,
        long? seqEnd = null)
    {
        if (_open.TryGetValue(signature, out var existing))
        {
            var updated = new ProblemRecord
            {
                ProblemId = existing.ProblemId,
                Signature = existing.Signature,
                Severity = severity,
                Status = ProblemStatuses.Open,
                FirstSeenAt = existing.FirstSeenAt,
                LastSeenAt = evt.Timestamp,
                Occurrences = existing.Occurrences + 1,
                Summary = summary,
                Expected = expected,
                Actual = actual,
                CaseId = caseId ?? existing.CaseId,
                OperationId = operationId ?? existing.OperationId,
                EventSequenceStart = existing.EventSequenceStart,
                EventSequenceEnd = seqEnd ?? evt.Sequence,
                ReplayCommand = existing.ReplayCommand,
                UserNoteRef = existing.UserNoteRef,
            };
            _open[signature] = updated;
            AppendProblem(updated);
            return;
        }

        var problem = new ProblemRecord
        {
            ProblemId = Ulid.NewUlid(evt.Timestamp),
            Signature = signature,
            Severity = severity,
            Status = ProblemStatuses.Open,
            FirstSeenAt = evt.Timestamp,
            LastSeenAt = evt.Timestamp,
            Occurrences = 1,
            Summary = summary,
            Expected = expected,
            Actual = actual,
            CaseId = caseId,
            OperationId = operationId,
            EventSequenceStart = seqStart ?? evt.Sequence,
            EventSequenceEnd = seqEnd ?? evt.Sequence,
            ReplayCommand = $"pwsh -File .\\dev\\dogfood.ps1 # runId={_runId} seq={seqStart ?? evt.Sequence}-{seqEnd ?? evt.Sequence}",
            UserNoteRef = null,
        };
        _open[signature] = problem;
        AppendProblem(problem);
    }

    private void AppendProblem(ProblemRecord problem)
    {
        _history.Add(problem);
        var line = JsonSerializer.Serialize(problem, RelayJson.Compact) + Environment.NewLine;
        File.AppendAllText(_problemsPath, line, Encoding.UTF8);
        WriteLatestMarkdown();
    }

    private void WriteLatestMarkdown()
    {
        var open = _open.Values
            .OrderByDescending(p => SeverityRank(p.Severity))
            .ThenBy(p => p.FirstSeenAt)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# RELAY latest problems");
        sb.AppendLine();
        sb.AppendLine($"runId: `{_runId}`");
        sb.AppendLine($"generatedAt: `{DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)}`");
        sb.AppendLine($"openCount: {open.Count}");
        sb.AppendLine();
        if (open.Count == 0)
        {
            sb.AppendLine("No open problems.");
        }
        else
        {
            foreach (var p in open)
            {
                sb.AppendLine($"## {p.Signature}");
                sb.AppendLine();
                sb.AppendLine($"- problemId: `{p.ProblemId}`");
                sb.AppendLine($"- severity: `{p.Severity}`");
                sb.AppendLine($"- status: `{p.Status}`");
                sb.AppendLine($"- occurrences: {p.Occurrences}");
                sb.AppendLine($"- eventSequence: `{p.EventSequenceStart}-{p.EventSequenceEnd}`");
                if (!string.IsNullOrWhiteSpace(p.CaseId)) sb.AppendLine($"- caseId: `{p.CaseId}`");
                if (!string.IsNullOrWhiteSpace(p.OperationId)) sb.AppendLine($"- operationId: `{p.OperationId}`");
                if (!string.IsNullOrWhiteSpace(p.ReplayCommand)) sb.AppendLine($"- replayCommand: `{p.ReplayCommand}`");
                if (!string.IsNullOrWhiteSpace(p.UserNoteRef)) sb.AppendLine($"- userNoteRef: `{p.UserNoteRef}`");
                sb.AppendLine($"- summary: {p.Summary}");
                if (!string.IsNullOrWhiteSpace(p.Expected)) sb.AppendLine($"- expected: {p.Expected}");
                if (!string.IsNullOrWhiteSpace(p.Actual)) sb.AppendLine($"- actual: {p.Actual}");
                sb.AppendLine();
            }
        }

        AtomicFile.WriteAllText(_latestPath, sb.ToString());
    }

    private static int SeverityRank(string severity) => severity switch
    {
        ProblemSeverities.Critical => 4,
        ProblemSeverities.Error => 3,
        ProblemSeverities.Warn => 2,
        _ => 1,
    };

    private static string CommandKey(ProductEvent evt) =>
        evt.Properties.GetValueOrDefault("commandId")
        ?? evt.OperationId
        ?? evt.CaseId
        ?? evt.Sequence.ToString(CultureInfo.InvariantCulture);
}
