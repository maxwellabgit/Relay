using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Ids;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.Storage;

namespace Relay.Core.Model;

/// <summary>
/// The model-backed orchestrator. The model never touches anything: it answers with one JSON
/// object per turn that is either a tool call (served by the same read-only <see cref="ToolBroker"/>
/// the rules use) or a final plan with an answer, citations, and proposals. Citations are accepted
/// only for ids that actually came back from a tool during this turn, so the model cannot invent
/// sources; proposals are passed to the policy engine exactly like any other producer's.
/// Every request and response is reported to the sink, so the reasoning trail is in the ledger.
/// </summary>
public sealed class ModelOrchestrator : IOrchestrator
{
    public const string ProducerPrefix = "model:";
    private const int MaxToolResultChars = 12_000;

    private readonly IModelClient _client;
    private readonly int _maxOutputTokens;

    public ModelOrchestrator(IModelClient client, int maxOutputTokens = 1500)
    {
        _client = client;
        _maxOutputTokens = maxOutputTokens;
    }

    public string Name => ProducerPrefix + _client.Model;

    public async Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage>
        {
            new("system", SystemPrompt()),
            new("user", UserPrompt(request, context)),
        };
        var seenHits = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        var toolCalls = 0;
        string? raw = null;

        for (var iteration = 0; iteration <= context.Settings.MaxToolCalls; iteration++)
        {
            var promptChars = messages.Sum(m => m.Content.Length);
            context.Sink.ModelRequested(_client.Host, _client.Model, promptChars, seenHits.Count);
            var response = await _client.CompleteAsync(new ModelRequest(_client.Model, messages, _maxOutputTokens, JsonObject: true), cancellationToken).ConfigureAwait(false);
            context.Sink.ModelResponded(response.Ok, response.Content?.Length ?? 0, response.ElapsedMs, response.Error);
            if (!response.Ok) return Fail($"Model unavailable: {response.Error}", raw);
            raw = response.Content ?? "";

            JsonObject message;
            try { message = JsonNode.Parse(raw)?.AsObject() ?? throw new JsonException("not an object"); }
            catch (JsonException ex) { return Fail("Model returned something other than the JSON contract: " + ex.Message, raw); }

            var type = message["type"]?.GetValue<string>();
            if (type == "tool")
            {
                if (toolCalls >= context.Settings.MaxToolCalls) return Fail($"Model exceeded the tool budget of {context.Settings.MaxToolCalls}.", raw);
                toolCalls++;
                var name = message["name"]?.GetValue<string>() ?? "";
                var args = new Dictionary<string, string>(StringComparer.Ordinal);
                if (message["args"] is JsonObject argObject)
                    foreach (var (k, v) in argObject) args[k] = v is JsonValue value ? value.ToString() : v?.ToJsonString() ?? "";
                if (name == "search" && !args.ContainsKey("exclude")) args["exclude"] = request.CaptureId;
                var result = context.Tools.Call(name, args);
                foreach (var hit in result.Hits ?? []) seenHits[hit.Id] = hit;
                messages.Add(new("assistant", raw));
                messages.Add(new("user", ToolResultMessage(name, result)));
                continue;
            }
            if (type == "final") return Final(message, request, context, seenHits, toolCalls, raw);
            return Fail($"Model returned an unknown message type '{type}'.", raw);
        }
        return Fail("Model did not finish within the tool budget.", raw);
    }

    private TurnPlan Fail(string summary, string? raw) => new(false, summary, ["Asked the model", summary], null, [], [], Name, raw);

    private TurnPlan Final(JsonObject message, TurnRequest request, TurnContext context, Dictionary<string, SearchHit> seenHits, int toolCalls, string raw)
    {
        var steps = new List<string> { $"Model planned with {toolCalls} tool call(s)" };
        foreach (var step in message["steps"]?.AsArray() ?? []) if (step?.GetValue<string>() is { Length: > 0 } s) steps.Add(Truncate(s, 200));
        var summary = Truncate(message["summary"]?.GetValue<string>() ?? "Model plan", 200);
        var answer = message["answer"]?.GetValue<string>();

        var citations = new List<Citation>();
        foreach (var node in message["citations"]?.AsArray() ?? [])
        {
            var id = node?["id"]?.GetValue<string>();
            if (id is not null && seenHits.TryGetValue(id, out var hit))
                citations.Add(new Citation(hit.Kind, hit.Id, hit.ProjectId, hit.ProjectSlug, hit.Excerpt, hit.Span));
            else if (id is not null) steps.Add($"Dropped a citation to '{id}': it was not returned by any tool this turn");
        }

        var proposals = new List<Proposal>();
        foreach (var node in message["proposals"]?.AsArray() ?? [])
        {
            if (node is not JsonObject p) continue;
            var action = p["action"]?.GetValue<string>() ?? "";
            var target = new Dictionary<string, string>(StringComparer.Ordinal);
            if (p["target"] is JsonObject t)
                foreach (var (k, v) in t) target[k] = v is JsonValue value ? value.ToString() : v?.ToJsonString() ?? "";
            var effects = (p["expected_effects"]?.AsArray() ?? []).Select(e => Truncate(e?.GetValue<string>() ?? "", 200)).Where(e => e.Length > 0).ToList();
            var tier = PolicyEngine.TierOf(action);
            var risk = tier == Tier.Prohibited ? Risks.Prohibited : Risks.ControlledWrite;
            proposals.Add(new Proposal(Ulid.NewUlid(request.At), action, Truncate(p["reason"]?.GetValue<string>() ?? "Proposed by the model.", 400), target,
                [request.SourceEventId], effects, risk, tier != Tier.Automatic, Name));
        }
        if (proposals.Count > 0) steps.Add($"{proposals.Count} proposal(s) handed to policy");
        return new TurnPlan(true, summary, steps, answer, citations, proposals, Name, raw);
    }

    private static string ToolResultMessage(string name, ToolResult result)
    {
        var sb = new StringBuilder();
        sb.Append("TOOL RESULT for ").Append(name).Append(": ").Append(result.Ok ? "ok" : "error").Append('\n');
        if (!result.Ok) sb.Append(result.Error);
        else
        {
            sb.Append(result.Summary).Append('\n');
            var json = result.Data is null ? "" : JsonSerializer.Serialize(result.Data, RelayJson.Compact);
            sb.Append(json.Length > MaxToolResultChars ? json[..MaxToolResultChars] + "…(truncated)" : json);
        }
        sb.Append("\nReply with the next JSON message.");
        return sb.ToString();
    }

    public static string SystemPrompt()
    {
        var sb = new StringBuilder();
        sb.Append("You are the planner inside Relay, a local-first personal orchestrator. You never perform actions. ");
        sb.Append("You read what the user said, optionally call read-only tools, and finish with a plan that deterministic software will check and, if approved by the user, execute.\n\n");
        sb.Append("Reply with exactly one JSON object and nothing else. Two shapes are allowed:\n");
        sb.Append("1. Tool call: {\"type\":\"tool\",\"name\":\"<tool>\",\"args\":{...}}\n");
        sb.Append("2. Final: {\"type\":\"final\",\"summary\":\"<one line>\",\"steps\":[\"<what you did/why>\"],\"answer\":\"<text for the user or null>\",");
        sb.Append("\"citations\":[{\"id\":\"<id returned by a tool>\"}],\"proposals\":[{\"action\":\"<action>\",\"reason\":\"<why>\",\"target\":{...},\"expected_effects\":[\"...\"]}]}\n\n");
        sb.Append("Tools (read-only):\n");
        foreach (var d in ToolBroker.Descriptors) sb.Append("- ").Append(d.Name).Append('(').Append(string.Join(", ", d.Arguments)).Append("): ").Append(d.Description).Append('\n');
        sb.Append("\nActions you may propose (policy decides; the user approves anything that changes a project):\n");
        sb.Append("- create_project {name, slug?}\n- archive_project {projectId}\n- restore_project {projectId}\n- rename_project {projectId, newName}\n");
        sb.Append("- route_note {noteId, projectId, type?, confidence}\n- create_draft_note {text, type, captureId}\n- modify_note {projectId, noteId, body?, status?, type?}\n");
        sb.Append("- supersede_note {projectId, noteId, supersededBy}\n- launch_worker {projectId, task:\"summarize\", objective}\n- apply_patch {projectId, runId, output, destination}\n- export_backup {path?}\n\n");
        sb.Append("Rules: cite only ids that a tool returned in this conversation; never invent ids, paths, or facts; ");
        sb.Append("prefer answering from tool results over guessing; when the instruction is ambiguous, say so in the answer and propose nothing; ");
        sb.Append("deletion does not exist, archive instead; keep summary and steps short and factual.");
        return sb.ToString();
    }

    private static string UserPrompt(TurnRequest request, TurnContext context)
    {
        var sb = new StringBuilder();
        sb.Append("Date: ").Append(request.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC\n");
        sb.Append("Active projects: ");
        var active = context.Registry.Active;
        sb.Append(active.Any() ? string.Join("; ", active.Select(p => $"{p.Name} (id {p.Id}, slug {p.Slug})")) : "none").Append('\n');
        sb.Append("Current capture id: ").Append(request.CaptureId).Append('\n');
        sb.Append("Instruction from the user:\n").Append(request.Instruction);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
