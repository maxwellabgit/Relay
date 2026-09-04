using System.Text.Json.Nodes;
using Relay.Core.Model;

namespace Relay.Tests.Support;

/// <summary>
/// A model that answers from a script. Each entry is either a JSON message the "model" returns or a
/// failure; every request is recorded so tests can inspect exactly what prompt Relay assembled.
/// </summary>
public sealed class ScriptedModelClient : IModelClient
{
    private readonly Queue<Func<ModelRequest, ModelResponse>> _script = new();

    public string Host => "model.test";
    public string Model => "test-model";
    public List<ModelRequest> Requests { get; } = new();

    public ScriptedModelClient Reply(string content) { _script.Enqueue(_ => new ModelResponse(true, content, 100, 20, 5, null, 200)); return this; }
    public ScriptedModelClient Reply(JsonObject content) => Reply(content.ToJsonString());
    public ScriptedModelClient Fail(string error, int? status = null) { _script.Enqueue(_ => ModelResponse.Failed(error, 7, status)); return this; }
    public ScriptedModelClient Always(Func<ModelRequest, string> reply) { _always = r => new ModelResponse(true, reply(r), 100, 20, 5, null, 200); return this; }
    private Func<ModelRequest, ModelResponse>? _always;

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_script.Count > 0) return Task.FromResult(_script.Dequeue()(request));
        if (_always is not null) return Task.FromResult(_always(request));
        return Task.FromResult(ModelResponse.Failed("script exhausted", 0));
    }

    public static JsonObject Tool(string name, params (string Key, string Value)[] args)
    {
        var a = new JsonObject();
        foreach (var (k, v) in args) a[k] = v;
        return new JsonObject { ["type"] = "tool", ["name"] = name, ["args"] = a };
    }

    public static JsonObject Final(string summary, string? answer = null, IEnumerable<string>? citations = null, params JsonObject[] proposals)
        => new()
        {
            ["type"] = "final",
            ["summary"] = summary,
            ["steps"] = new JsonArray("Looked at the request"),
            ["answer"] = answer,
            ["citations"] = new JsonArray((citations ?? []).Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()),
            ["proposals"] = new JsonArray(proposals.Select(p => (JsonNode?)p).ToArray()),
        };

    public static JsonObject Proposal(string action, string reason, params (string Key, string Value)[] target)
    {
        var t = new JsonObject();
        foreach (var (k, v) in target) t[k] = v;
        return new JsonObject { ["action"] = action, ["reason"] = reason, ["target"] = t, ["expected_effects"] = new JsonArray("Something changes") };
    }
}
