using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relay.Core.Agents;

/// <summary>What the broker learned from one worker line: an optional reply to send back, and whether the run is over.</summary>
public sealed record BrokerTurn(string? Reply, bool Finished, string? Log);

public sealed record BrokerEvent(string Type, object Data);

/// <summary>
/// The only surface a worker has. Every tool call is checked against the run's tool allowlist and
/// file allowlists, confined to the staging folder, counted against the limits, and reported as
/// an event. The worker never learns a path outside its staging folder because none is accepted.
/// </summary>
public sealed class WorkerBroker
{
    private readonly AgentRunSpec _spec;
    private readonly string _stagingCanonical;
    private long _readBytes;
    private long _writeBytes;

    public WorkerBroker(AgentRunSpec spec)
    {
        _spec = spec;
        _stagingCanonical = Path.GetFullPath(spec.StagingPath).TrimEnd(Path.DirectorySeparatorChar);
    }

    public int ToolCalls { get; private set; }
    public int Denied { get; private set; }
    public bool Done { get; private set; }
    public bool Failed { get; private set; }
    public string? Summary { get; private set; }
    public string? Error { get; private set; }
    public List<string> Outputs { get; } = new();
    public List<BrokerEvent> Events { get; } = new();

    /// <summary>Handles one line from the worker. Never throws on worker input; malformed lines are denied and counted.</summary>
    public BrokerTurn Handle(string line)
    {
        JsonObject message;
        try { message = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("not an object"); }
        catch (JsonException ex)
        {
            Denied++;
            Events.Add(new(Ledger.EventTypes.AgentRunToolDenied, new { runId = _spec.RunId, tool = "?", reason = "malformed line: " + ex.Message }));
            return new BrokerTurn(null, false, null);
        }

        switch (message["type"]?.GetValue<string>())
        {
            case "log":
                var text = Truncate(message["text"]?.GetValue<string>() ?? "", 300);
                return new BrokerTurn(null, false, text);
            case "done":
                Done = true;
                Summary = Truncate(message["summary"]?.GetValue<string>() ?? "done", 500);
                foreach (var o in message["outputs"]?.AsArray() ?? []) if (o?.GetValue<string>() is { } s) Outputs.Add(s);
                return new BrokerTurn(null, true, null);
            case "failed":
                Failed = true;
                Error = Truncate(message["error"]?.GetValue<string>() ?? "worker reported failure", 500);
                return new BrokerTurn(null, true, null);
            case "call":
                return new BrokerTurn(Call(message), false, null);
            default:
                Denied++;
                Events.Add(new(Ledger.EventTypes.AgentRunToolDenied, new { runId = _spec.RunId, tool = "?", reason = "unknown message type" }));
                return new BrokerTurn(null, false, null);
        }
    }

    private string Call(JsonObject message)
    {
        var id = message["id"]?.GetValue<int>() ?? 0;
        var tool = message["tool"]?.GetValue<string>() ?? "";
        var args = message["args"]?.AsObject() ?? new JsonObject();
        var path = args["path"]?.GetValue<string>() ?? "";

        string Deny(string reason)
        {
            Denied++;
            Events.Add(new(Ledger.EventTypes.AgentRunToolDenied, new { runId = _spec.RunId, tool, path, reason }));
            return Reply(id, false, null, reason);
        }

        if (ToolCalls >= _spec.Limits.MaxToolCalls) return Deny($"tool call limit of {_spec.Limits.MaxToolCalls} reached");
        ToolCalls++;
        if (!_spec.ToolAllowlist.Contains(tool, StringComparer.Ordinal)) return Deny($"tool '{tool}' is not in this run's allowlist");

        var resolved = Resolve(path);
        if (resolved is null) return Deny($"path '{path}' is not a plain relative path inside the staging folder");

        try
        {
            switch (tool)
            {
                case "list_dir":
                    if (!Allowed(path, _spec.ReadAllow, isDirectory: true)) return Deny($"'{path}' is outside the read allowlist");
                    if (!Directory.Exists(resolved)) return Deny($"'{path}' does not exist");
                    var files = Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(_stagingCanonical, f).Replace(Path.DirectorySeparatorChar, '/'))
                        .OrderBy(f => f, StringComparer.Ordinal).ToList();
                    Events.Add(new(Ledger.EventTypes.AgentRunToolCalled, new { runId = _spec.RunId, tool, path, ok = true, items = files.Count }));
                    return Reply(id, true, string.Join('\n', files), null);

                case "read_file":
                    if (!Allowed(path, _spec.ReadAllow, isDirectory: false)) return Deny($"'{path}' is outside the read allowlist");
                    if (!File.Exists(resolved)) return Deny($"'{path}' does not exist");
                    var info = new FileInfo(resolved);
                    if (_readBytes + info.Length > _spec.Limits.MaxReadBytes) return Deny($"read budget of {_spec.Limits.MaxReadBytes} bytes exhausted");
                    _readBytes += info.Length;
                    var content = File.ReadAllText(resolved, Encoding.UTF8);
                    Events.Add(new(Ledger.EventTypes.AgentRunToolCalled, new { runId = _spec.RunId, tool, path, ok = true, bytes = info.Length }));
                    return Reply(id, true, content, null);

                case "write_file":
                    if (!Allowed(path, _spec.WriteAllow, isDirectory: false)) return Deny($"'{path}' is outside the write allowlist");
                    var textToWrite = args["text"]?.GetValue<string>() ?? "";
                    var bytes = Encoding.UTF8.GetByteCount(textToWrite);
                    if (_writeBytes + bytes > _spec.Limits.MaxWriteBytes) return Deny($"write budget of {_spec.Limits.MaxWriteBytes} bytes exhausted");
                    _writeBytes += bytes;
                    Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                    File.WriteAllText(resolved, textToWrite, new UTF8Encoding(false));
                    Events.Add(new(Ledger.EventTypes.AgentRunToolCalled, new { runId = _spec.RunId, tool, path, ok = true, bytes }));
                    return Reply(id, true, "ok", null);

                default:
                    return Deny($"tool '{tool}' exists in the allowlist but has no implementation");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Events.Add(new(Ledger.EventTypes.AgentRunToolCalled, new { runId = _spec.RunId, tool, path, ok = false, error = ex.Message }));
            return Reply(id, false, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Accepts only plain relative paths; resolves them under staging and refuses anything that lands outside.</summary>
    private string? Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512) return null;
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal) || relative.StartsWith('\\') || relative.StartsWith('/')) return null;
        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == "." || p == ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        var full = Path.GetFullPath(Path.Combine(_stagingCanonical, Path.Combine(parts)));
        return full.StartsWith(_stagingCanonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Allowlist entries are "folder/**" prefixes or exact relative paths.</summary>
    private static bool Allowed(string relative, IReadOnlyList<string> allow, bool isDirectory)
    {
        var normalized = relative.Replace('\\', '/').Trim('/');
        foreach (var rule in allow)
        {
            if (rule.EndsWith("/**", StringComparison.Ordinal))
            {
                var prefix = rule[..^3];
                if (normalized == prefix || normalized.StartsWith(prefix + "/", StringComparison.Ordinal)) return true;
            }
            else if (!isDirectory && string.Equals(rule, normalized, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Reply(int id, bool ok, string? result, string? error)
        => new JsonObject { ["type"] = "result", ["id"] = id, ["ok"] = ok, ["result"] = result, ["error"] = error }.ToJsonString();

    public static string StopMessage(string reason) => new JsonObject { ["type"] = "stop", ["reason"] = reason }.ToJsonString();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
