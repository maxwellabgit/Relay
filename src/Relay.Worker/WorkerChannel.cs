using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relay.Worker;

/// <summary>One JSON line in, one JSON line out. Implemented over stdio in the process and over in-memory pipes in tests.</summary>
public interface IWorkerChannel
{
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
}

public sealed class StdioChannel : IWorkerChannel
{
    private readonly TextReader _in;
    private readonly TextWriter _out;

    public StdioChannel(TextReader input, TextWriter output) { _in = input; _out = output; }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => _in.ReadLineAsync(cancellationToken).AsTask();

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _out.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _out.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The worker side of the broker protocol: numbered tool calls, progress logs, and a final done/failed message.</summary>
public sealed class BrokerClient
{
    private readonly IWorkerChannel _channel;
    private readonly CancellationToken _ct;
    private int _nextId;

    public BrokerClient(IWorkerChannel channel, CancellationToken cancellationToken)
    {
        _channel = channel;
        _ct = cancellationToken;
    }

    public async Task<string> CallAsync(string tool, params (string Key, string Value)[] args)
    {
        var id = ++_nextId;
        var message = new JsonObject { ["type"] = "call", ["id"] = id, ["tool"] = tool, ["args"] = new JsonObject(args.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)a.Value))) };
        await _channel.WriteLineAsync(message.ToJsonString(), _ct).ConfigureAwait(false);
        while (true)
        {
            var line = await _channel.ReadLineAsync(_ct).ConfigureAwait(false) ?? throw new EndOfStreamException("Broker closed the channel.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var reply = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("Broker sent a non-object line.");
            if (reply["type"]?.GetValue<string>() == "stop") throw new OperationCanceledException(reply["reason"]?.GetValue<string>() ?? "stopped by Relay");
            if (reply["type"]?.GetValue<string>() != "result" || reply["id"]?.GetValue<int>() != id) continue;
            if (reply["ok"]?.GetValue<bool>() == true) return reply["result"]?.GetValue<string>() ?? "";
            throw new BrokerDeniedException(tool, reply["error"]?.GetValue<string>() ?? "denied");
        }
    }

    public Task LogAsync(string text) => _channel.WriteLineAsync(new JsonObject { ["type"] = "log", ["text"] = text }.ToJsonString(), _ct);

    public Task DoneAsync(string summary, IEnumerable<string> outputs)
        => _channel.WriteLineAsync(new JsonObject { ["type"] = "done", ["summary"] = summary, ["outputs"] = new JsonArray(outputs.Select(o => (JsonNode?)o).ToArray()) }.ToJsonString(), _ct);

    public Task FailedAsync(string error) => _channel.WriteLineAsync(new JsonObject { ["type"] = "failed", ["error"] = error }.ToJsonString(), _ct);
}

public sealed class BrokerDeniedException(string tool, string reason) : Exception($"{tool}: {reason}")
{
    public string Tool { get; } = tool;
}

/// <summary>The first message from the broker: what this run is and what it may touch.</summary>
public sealed record RunSpecMessage(string RunId, string Task, string Objective, string ProjectSlug, IReadOnlyList<string> Inputs, IReadOnlyList<string> RequiredOutputs)
{
    public static RunSpecMessage Parse(string line)
    {
        var node = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("Spec line is not a JSON object.");
        if (node["type"]?.GetValue<string>() != "spec") throw new InvalidDataException("First message must be the run spec.");
        return new RunSpecMessage(
            node["runId"]?.GetValue<string>() ?? "",
            node["task"]?.GetValue<string>() ?? "",
            node["objective"]?.GetValue<string>() ?? "",
            node["projectSlug"]?.GetValue<string>() ?? "",
            node["inputs"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [],
            node["requiredOutputs"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? []);
    }
}
