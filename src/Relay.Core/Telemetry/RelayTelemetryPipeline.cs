namespace Relay.Core.Telemetry;

/// <summary>
/// Production telemetry pipeline: JSONL sink + deterministic problem detection.
/// RELAY records facts; detectors create problems; Cursor analyzes them.
/// </summary>
public sealed class RelayTelemetryPipeline : IRelayTelemetry
{
    private readonly JsonlRelayTelemetry _jsonl;
    private readonly ProblemDetector _detector;

    public RelayTelemetryPipeline(string runDir, string runId, string? appVersion = null)
    {
        _jsonl = new JsonlRelayTelemetry(runDir, runId, appVersion);
        _detector = new ProblemDetector(runDir, runId);
    }

    public string RunId => _jsonl.RunId;
    public string? RunDir => _jsonl.RunDir;
    public long LastSequence => _jsonl.LastSequence;
    public ProblemDetector Detector => _detector;
    public string EventsPath => _jsonl.EventsPath;
    public string LatestProblemsPath => _detector.LatestProblemsPath;

    public ProductEvent Emit(ProductEventDraft draft)
    {
        var evt = _jsonl.Emit(draft);
        _detector.Observe(evt);
        return evt;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _jsonl.Dispose();
    }
}
