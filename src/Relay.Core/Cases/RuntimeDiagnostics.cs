using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Cases;

/// <summary>One structured diagnostics line written to a JSONL file.</summary>
public sealed class RuntimeDiagnosticEvent
{
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; init; }
    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("level")] public required string Level { get; init; }
    [JsonPropertyName("component")] public required string Component { get; init; }
    [JsonPropertyName("event")] public required string Event { get; init; }
    [JsonPropertyName("caseId")] public string? CaseId { get; init; }
    [JsonPropertyName("caseVersion")] public long? CaseVersion { get; init; }
    [JsonPropertyName("operationId")] public string? OperationId { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("latencyMs")] public long? LatencyMs { get; init; }
    [JsonPropertyName("promptRef")] public string? PromptRef { get; init; }
    [JsonPropertyName("resultRef")] public string? ResultRef { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>Append-only structured JSONL diagnostics for a single run.</summary>
public sealed class RuntimeDiagnostics : IDisposable
{
    private readonly string _path;
    private readonly string _runId;
    private readonly object _gate = new();
    private long _seq;
    private FileStream? _stream;
    private bool _disposed;

    public RuntimeDiagnostics(string path, string runId)
    {
        _path = path;
        _runId = runId;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    public string Path => _path;
    public string RunId => _runId;

    public void Write(
        DateTimeOffset ts,
        string level,
        string component,
        string eventName,
        string? caseId = null,
        long? caseVersion = null,
        string? operationId = null,
        string? status = null,
        long? latencyMs = null,
        string? promptRef = null,
        string? resultRef = null,
        string? error = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var evt = new RuntimeDiagnosticEvent
            {
                Ts = ts,
                RunId = _runId,
                Seq = ++_seq,
                Level = level,
                Component = component,
                Event = eventName,
                CaseId = caseId,
                CaseVersion = caseVersion,
                OperationId = operationId,
                Status = status,
                LatencyMs = latencyMs,
                PromptRef = promptRef,
                ResultRef = resultRef,
                Error = error,
            };
            var line = JsonSerializer.Serialize(evt, RelayJson.Compact) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            _stream!.Write(bytes);
            _stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }
}
