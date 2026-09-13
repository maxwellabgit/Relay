using System.Diagnostics;
using Relay.Core.Model;

namespace Relay.Core.Mind;

/// <summary>
/// The mind on a model: one schema-constrained chat completion per step over the rendered transcript.
/// Any <see cref="IModelClient"/> serves — the local Ministral is the placeholder for whichever model
/// runs the loop. Failures are data (<see cref="MindStep.Error"/>); the loop decides about retries.
/// </summary>
public sealed class ModelMind : IMind
{
    public const string NamePrefix = "mind:";

    private readonly IModelClient _client;
    private readonly int _maxOutputTokens;

    public ModelMind(IModelClient client, int maxOutputTokens = 700)
    {
        _client = client;
        _maxOutputTokens = Math.Max(200, maxOutputTokens);
    }

    public string Name => NamePrefix + _client.Model;
    public string Host => _client.Host;
    public string Model => _client.Model;

    public async Task<MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var messages = new List<ModelMessage>
        {
            new("system", MindPrompt.System(request.Context, request.Observing)),
            new("user", MindPrompt.Transcript(request)),
        };
        var promptChars = messages.Sum(m => m.Content.Length);
        var schema = request.Observing ? MoveSchema.ObservingJson : MoveSchema.Json;
        var schemaName = request.Observing ? MoveSchema.ObservingSchemaName : MoveSchema.SchemaName;
        var response = await _client.CompleteAsync(new ModelRequest(_client.Model, messages, _maxOutputTokens, JsonObject: true, JsonSchema: schema, SchemaName: schemaName), cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return MindStep.Failed("Model unavailable: " + response.Error, null, promptChars, watch.ElapsedMilliseconds, response.PromptTokens, response.CompletionTokens);
        var raw = response.Content ?? "";
        try
        {
            return MoveSchema.Parse(raw, promptChars, watch.ElapsedMilliseconds, response.PromptTokens, response.CompletionTokens);
        }
        catch (FormatException ex)
        {
            return MindStep.Failed("Contract: " + ex.Message, raw, promptChars, watch.ElapsedMilliseconds, response.PromptTokens, response.CompletionTokens);
        }
    }
}

/// <summary>
/// A mind that answers from a script, for scenario tests and evaluation cases. Each entry is a step
/// built in code, a raw JSON reply (which goes through the real parser), or a failure; every request
/// is kept so a test can read exactly what transcript the loop presented.
/// </summary>
public sealed class ScriptedMind : IMind
{
    private readonly Queue<Func<MindRequest, MindStep>> _script = new();
    private Func<MindRequest, MindStep>? _always;

    public string Name => "mind:scripted";
    public List<MindRequest> Requests { get; } = new();

    public ScriptedMind Step(Move move, string? feed = null, MindRead? read = null) { _script.Enqueue(_ => MindStep.Of(move, feed, read)); return this; }
    public ScriptedMind Raw(string json) { _script.Enqueue(_ => Parse(json)); return this; }
    public ScriptedMind Fail(string error) { _script.Enqueue(_ => MindStep.Failed(error, null, 0, 1)); return this; }
    public ScriptedMind Then(Func<MindRequest, MindStep> step) { _script.Enqueue(step); return this; }
    /// <summary>Answers every request the script does not cover.</summary>
    public ScriptedMind Always(Func<MindRequest, MindStep> step) { _always = step; return this; }

    public Task<MindStep> StepAsync(MindRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_script.Count > 0) return Task.FromResult(_script.Dequeue()(request));
        if (_always is not null) return Task.FromResult(_always(request));
        return Task.FromResult(MindStep.Failed("script exhausted", null, 0, 0));
    }

    private static MindStep Parse(string json)
    {
        try { return MoveSchema.Parse(json); }
        catch (FormatException ex) { return MindStep.Failed("Contract: " + ex.Message, json, 0, 0); }
    }

    // Shorthands for readable scripts.
    public static SayMove Say(string text, bool done = true) => new(text, done);
    public static UseToolMove Tool(string name, params (string Key, string Value)[] args) => new(name, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));
    public static ProposeMove Propose(string action, string reason, params (string Key, string Value)[] target) => new(action, target.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal), reason);
    public static DelegateMove Delegate(string profile, string prompt, int budget = 2_000, bool search = false, params string[] refs) => new(profile, prompt, refs, budget, search);
    /// <summary>The next turn of a delegate conversation that returned (slice 5): under the first request's approval, within its bounds.</summary>
    public static DelegateMove Reply(string requestId, string prompt, params string[] refs) => new("", prompt, refs, MoveSchema.DefaultDelegateBudget, false, requestId);
    public static BuildMove Build(string name, string justification, string inputs = "", string outputs = "") => new(name, justification, inputs, outputs);
    public static RunWorkflowMove RunWorkflow(string name, params (string Key, string Value)[] args) => new(name, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));
    public static AskUserMove Ask(string question, params string[] options) => new(question, options);
    public static WaitMove Wait(string reason = "nothing to do") => new(reason);
    public static StopMove Stop(string reason = "stop") => new(reason);
    /// <summary>A listening pass that found something: the labels are the window's, resolved by the host.</summary>
    public static RaiseMove Raise(string kind, string objective, string segments = "#1", string? note = null, string? noteType = null, string? project = null)
        => new(kind, objective, segments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "it matters", note, noteType, project);
    public static MindRead Read(double complexity, params string[] needs) => new("", complexity, needs.Length == 0 ? [MindRead.NeedNone] : needs, 0, 0, RiskRead.None);
    /// <summary>The read of a task that has now seen enough to say whether the claim agrees with the record.</summary>
    public static MindRead Verdict(bool consistent) => new("checking a claim against the record", 0.3, [MindRead.NeedLocalNotes], 0, 0, RiskRead.None, consistent);
}
