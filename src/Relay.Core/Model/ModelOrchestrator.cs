using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Ids;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.Storage;
using Relay.Core.Tasks;

namespace Relay.Core.Model;

/// <summary>
/// RELAY0 as planner. The model never touches anything: it answers with one JSON object per turn that
/// is either a tool call (served by the read-only <see cref="ToolBroker"/>) or a final plan with an
/// answer, citations, a knowledge state, and proposals. Citations are accepted only for ids that came
/// back from a tool during this task, so the model cannot invent sources; proposals go to the policy
/// engine exactly like any other producer's. Every request and response is reported to the sink.
/// </summary>
public sealed class ModelOrchestrator : IOrchestrator
{
    public const string ProducerPrefix = "model:";
    private const int MaxToolResultChars = 12_000;

    private readonly IModelClient _client;
    private readonly int _maxOutputTokens;

    public ModelOrchestrator(IModelClient client, int maxOutputTokens = 800)
    {
        _client = client;
        _maxOutputTokens = maxOutputTokens;
    }

    public string Name => ProducerPrefix + _client.Model;

    public async Task<TurnPlan> PlanAsync(TurnRequest request, TurnContext context, CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage>
        {
            new("system", SystemPrompt(context)),
            new("user", UserPrompt(request, context)),
        };
        var seenHits = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        var toolCalls = 0;
        string? raw = null;
        var maxTokens = Math.Min(_maxOutputTokens, Math.Max(200, context.Preferences?.MaxAnswerTokens + 300 ?? _maxOutputTokens));

        for (var iteration = 0; iteration <= context.Settings.MaxToolCalls; iteration++)
        {
            var promptChars = messages.Sum(m => m.Content.Length);
            context.Sink.ModelRequested(_client.Host, _client.Model, promptChars, seenHits.Count);
            var response = await _client.CompleteAsync(new ModelRequest(_client.Model, messages, maxTokens, JsonObject: true), cancellationToken).ConfigureAwait(false);
            context.Sink.ModelResponded(response.Ok, response.Content?.Length ?? 0, response.ElapsedMs, response.Error, response.PromptTokens, response.CompletionTokens);
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
                if (name == "search" && !args.ContainsKey("exclude") && request.CaptureId.Length > 0) args["exclude"] = request.CaptureId;
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
        if (answer is not null && context.Preferences is { } prefs && answer.Length > prefs.MaxAnswerChars)
        {
            answer = answer[..prefs.MaxAnswerChars].TrimEnd() + "…";
            steps.Add($"Answer cut to the preferred length ({prefs.MaxAnswerChars} chars)");
        }

        var citations = new List<Citation>();
        foreach (var node in message["citations"]?.AsArray() ?? [])
        {
            var id = node?["id"]?.GetValue<string>();
            if (id is not null && seenHits.TryGetValue(id, out var hit))
                citations.Add(new Citation(hit.Kind, hit.Id, hit.ProjectId, hit.ProjectSlug, hit.Excerpt, hit.Span));
            else if (id is not null) steps.Add($"Dropped a citation to '{id}': it was not returned by any tool this task");
        }

        KnowledgeState? knowledge = null;
        if (message["knowledge"] is JsonObject k)
        {
            var known = (k["known"]?.AsArray() ?? []).Select(n => n?.GetValue<string>() ?? "").Where(id => seenHits.ContainsKey(id)).ToList();
            var missing = (k["missing"]?.AsArray() ?? []).Select(n => Truncate(n?.GetValue<string>() ?? "", 160)).Where(s => s.Length > 0).ToList();
            knowledge = new KnowledgeState(known, missing, k["capability_gap"]?.GetValue<bool>() ?? false, Truncate(k["summary"]?.GetValue<string>() ?? "", 200));
        }
        bool? consistent = message["consistent"] is JsonValue cv && cv.TryGetValue<bool>(out var c) ? c : null;

        var proposals = new List<Proposal>();
        var localIds = new Dictionary<string, string>(StringComparer.Ordinal); // model-side "id" → proposal id, for depends_on
        foreach (var node in message["proposals"]?.AsArray() ?? [])
        {
            if (node is not JsonObject p) continue;
            var action = p["action"]?.GetValue<string>() ?? "";
            var target = new Dictionary<string, string>(StringComparer.Ordinal);
            if (p["target"] is JsonObject t)
                foreach (var (key, v) in t) target[key] = v is JsonValue value ? value.ToString() : v?.ToJsonString() ?? "";
            var effects = (p["expected_effects"]?.AsArray() ?? []).Select(e => Truncate(e?.GetValue<string>() ?? "", 200)).Where(e => e.Length > 0).ToList();
            var tier = PolicyEngine.TierOf(action);
            var risk = tier == Tier.Prohibited ? Risks.Prohibited : action == Actions.ModelRequest ? Risks.External : Risks.ControlledWrite;
            var id = Ulid.NewUlid(request.At);
            if (p["id"]?.GetValue<string>() is { Length: > 0 } local) localIds[local] = id;
            var depends = (p["depends_on"]?.AsArray() ?? []).Select(d => d?.GetValue<string>() ?? "").Where(d => d.Length > 0).ToList();
            proposals.Add(new Proposal(id, action, Truncate(p["reason"]?.GetValue<string>() ?? "Proposed by the model.", 400), target,
                [request.SourceEventId], effects, risk, tier != Tier.Automatic, Name, depends));
        }
        // Resolve depends_on from the model's local ids to real proposal ids; unknown references are dropped and reported.
        for (var i = 0; i < proposals.Count; i++)
        {
            var deps = proposals[i].Dependencies;
            if (deps.Count == 0) continue;
            var resolved = deps.Select(d => localIds.GetValueOrDefault(d)).Where(d => d is not null).Select(d => d!).ToList();
            if (resolved.Count != deps.Count) steps.Add("Dropped a dependency that named no proposal in this plan");
            proposals[i] = proposals[i] with { DependsOn = resolved };
        }
        if (proposals.Count > 0) steps.Add($"{proposals.Count} proposal(s) handed to policy");
        return new TurnPlan(true, summary, steps, answer, citations, proposals, Name, raw, knowledge, consistent);
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

    public static string SystemPrompt(TurnContext? context = null)
    {
        var sb = new StringBuilder();
        sb.Append("You are RELAY0's planner inside Relay, a local-first personal orchestrator. You never perform actions. ");
        sb.Append("You read the request, call read-only tools when facts are needed, and finish with a plan that deterministic software checks and, if the user approves, executes.\n\n");
        sb.Append("Reply with exactly one JSON object and nothing else. Two shapes are allowed:\n");
        sb.Append("1. Tool call: {\"type\":\"tool\",\"name\":\"<tool>\",\"args\":{...}}\n");
        sb.Append("2. Final: {\"type\":\"final\",\"summary\":\"<one line>\",\"steps\":[\"<what you did/why, short>\"],\"answer\":\"<text for the user or null>\",");
        sb.Append("\"citations\":[{\"id\":\"<id returned by a tool>\"}],\"consistent\":<true|false|null>,");
        sb.Append("\"knowledge\":{\"known\":[\"<ids you relied on>\"],\"missing\":[\"<facts with no local source>\"],\"capability_gap\":<true|false>,\"summary\":\"<one line>\"},");
        sb.Append("\"proposals\":[{\"id\":\"p1\",\"action\":\"<action>\",\"reason\":\"<why>\",\"target\":{...},\"expected_effects\":[\"...\"],\"depends_on\":[\"p0\"]}]}\n\n");
        sb.Append("Tools (read-only):\n");
        foreach (var d in ToolBroker.Descriptors) sb.Append("- ").Append(d.Name).Append('(').Append(string.Join(", ", d.Arguments)).Append("): ").Append(d.Description).Append('\n');
        sb.Append("\nActions you may propose (policy decides; the user approves anything that changes a project or Relay itself):\n");
        sb.Append("- create_project {name, slug?}\n- archive_project {projectId}\n- restore_project {projectId}\n- rename_project {projectId, newName}\n- delete_project {projectId, confirm:\"delete\"} only when the user explicitly asked to delete\n");
        sb.Append("- route_note {noteId, projectId, type?, confidence}\n- create_draft_note {text, type}\n- modify_note {projectId, noteId, body?, status?, type?}\n");
        sb.Append("- supersede_note {projectId, noteId, newText, type?, sourceExcerptId?} replaces a stored decision with new text in one step (or {projectId, noteId, supersededBy} to link two existing notes)\n- move_note {projectId, noteId, toProjectId} (or toProject:\"<name>\" when the destination is a create_project proposal in this same plan that the move depends_on)\n");
        sb.Append("- launch_worker {projectId, task:\"summarize\", objective}\n- apply_patch {projectId, runId, output, destination}\n- export_backup {path?}\n");
        sb.Append("- model.request {profile, objective, refs:\"<comma-separated ids returned by tools>\", budgetTokens, allowSearch} to delegate bounded work to a named external model");
        if (context is { ExternalProfiles.Count: > 0 }) sb.Append(" (profiles: ").Append(string.Join(", ", context.ExternalProfiles)).Append(')');
        sb.Append("\n- update_preference {key, value, benefit, permissions, scope, acceptance} for response.verbosity | response.promptLine | display.alwaysShow | display.stopShowing | display.maxAlertsPer10Minutes | display.maxResultsPer5Minutes | display.cooldownSeconds | filing.grant | filing.revoke | sources.allowOnlineSearch | retention.bufferSeconds | retention.excerptMaxSeconds\n");
        sb.Append("- update_prompt {name:\"planner\"|\"judge\", content, benefit, permissions, scope, acceptance}\n");
        sb.Append("  (benefit, permissions, scope, acceptance form the improvement contract: the concrete benefit to the user, what the change is allowed to touch, how much changes, and how the user can tell it worked; an improve task without all four is denied)\n\n");
        sb.Append("Rules: cite only ids that a tool returned in this task; never invent ids, paths, or facts; prefer tool results over guessing; ");
        sb.Append("for a check task, search first, set consistent, cite both the stored note and the excerpt, and when the stated fact conflicts with a stored decision propose supersede_note with newText (the corrected decision in one sentence) so the user can update the record with one approval; ");
        sb.Append("state the knowledge gap honestly: 'missing' is what has no local source, 'capability_gap' is true only when you cannot do the work even with the sources; propose model.request only when capability_gap is true and the user's request needs it; ");
        sb.Append("when several operations belong together give each an id and use depends_on so a prerequisite can be approved before its dependents; ");
        sb.Append("for a direct request about how Relay itself should behave propose update_preference with the typed key (and for response style also update_prompt planner: the current fragment plus the user's instruction as one line) rather than answering in prose; ");
        sb.Append("when the request is ambiguous say so in the answer and propose nothing; keep summary and steps short and factual.");
        if (context?.Preferences is { } prefs) sb.Append("\n\nResponse style (the user's preference; obey it): ").Append(prefs.PromptFragment);
        if (!string.IsNullOrWhiteSpace(context?.PromptFragment)) sb.Append("\n\nAdditional instructions approved by the user: ").Append(context!.PromptFragment);
        return sb.ToString();
    }

    private static string UserPrompt(TurnRequest request, TurnContext context)
    {
        var sb = new StringBuilder();
        sb.Append("Date: ").Append(request.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC\n");
        sb.Append("Active projects: ");
        var active = context.Registry.Active;
        sb.Append(active.Any() ? string.Join("; ", active.Select(p => $"{p.Name} (id {p.Id}, slug {p.Slug})")) : "none").Append('\n');
        sb.Append("Task origin: ").Append(request.Origin.Wire()).Append(" · kind: ").Append(request.Kind.Wire()).Append('\n');
        if (request.Origin == TaskOrigin.Observed) sb.Append("This task was raised by the judge from something overheard, not asked. Read the excerpt first (read_excerpt) if you need the exact words. Do not propose deletions, preference changes, or external requests.\n");
        if (request.ExcerptId is not null) sb.Append("Excerpt id: ").Append(request.ExcerptId).Append('\n');
        if (request.ArtifactId is not null) sb.Append("Artifact id: ").Append(request.ArtifactId).Append(" (read it with read_artifact; cite it; propose create_draft_note for findings worth keeping)\n");
        if (request.CaptureId.Length > 0) sb.Append("Current capture id: ").Append(request.CaptureId).Append('\n');
        sb.Append(request.Origin == TaskOrigin.Direct ? "Instruction from the user:\n" : "Focused prompt:\n").Append(request.Instruction);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
