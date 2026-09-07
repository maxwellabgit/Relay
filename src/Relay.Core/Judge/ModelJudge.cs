using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Model;
using Relay.Core.Tasks;

namespace Relay.Core.Judge;

/// <summary>
/// RELAY0 as judge: one schema-constrained JSON call over the rolling window that says whether
/// anything significant happened and, if so, what kind of task it is and which segments substantiate
/// it. The model sees the window and the session context — never files. Its findings are candidates:
/// the arbiter decides what is shown, policy decides what may run.
/// </summary>
public sealed class ModelJudge : IJudge
{
    private readonly IModelClient _client;
    private readonly int _maxOutputTokens;
    private readonly double _minConfidence;

    public ModelJudge(IModelClient client, int maxOutputTokens = 600, double minConfidence = 0.55)
    {
        _client = client;
        _maxOutputTokens = maxOutputTokens;
        _minConfidence = minConfidence;
    }

    public string Name => "model:" + _client.Model;

    public async Task<JudgeDecision> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var messages = new List<ModelMessage>
        {
            new("system", SystemPrompt(request.Context)),
            new("user", UserPrompt(request)),
        };
        var response = await _client.CompleteAsync(new ModelRequest(_client.Model, messages, _maxOutputTokens, JsonObject: true, JsonSchema: Schema, SchemaName: "judge_decision"), cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return JudgeDecision.Failed(Name, response.Error ?? "model unavailable", watch.ElapsedMilliseconds) with { PromptTokens = response.PromptTokens, CompletionTokens = response.CompletionTokens };

        var raw = response.Content ?? "";
        var findings = new List<JudgeFinding>();
        try
        {
            var root = JsonNode.Parse(raw)?.AsObject() ?? throw new JsonException("not an object");
            var known = new HashSet<string>(request.Window.Select(s => s.SegmentId), StringComparer.Ordinal);
            foreach (var node in root["findings"]?.AsArray() ?? [])
            {
                if (node is not JsonObject f) continue;
                var confidence = f["confidence"]?.GetValue<double>() ?? 0;
                if (confidence < _minConfidence) continue;
                var ids = (f["segment_ids"]?.AsArray() ?? []).Select(n => n?.GetValue<string>() ?? "").Where(known.Contains).Distinct().ToList();
                if (request.Origin != TaskOrigin.Direct && ids.Count == 0) continue; // an observed finding must point at words in the window
                var kind = TaskLanes.ParseKind(f["kind"]?.GetValue<string>());
                var summary = Truncate(f["summary"]?.GetValue<string>() ?? "", 96);
                var focused = f["focused_prompt"]?.GetValue<string>() ?? summary;
                if (string.IsNullOrWhiteSpace(focused)) continue;
                findings.Add(new JudgeFinding(kind, Math.Clamp(confidence, 0, 1), summary, Truncate(f["why"]?.GetValue<string>() ?? "", 80), focused, ids,
                    Blank(f["topic"]), Blank(f["project"]), TaskLanes.ParsePresentation(f["presentation"]?.GetValue<string>()),
                    Blank(f["note_text"]), Blank(f["note_type"]), Blank(f["merge_key"])));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new JudgeDecision([], Name, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds, "Judge returned something other than the contract: " + ex.Message, raw);
        }
        return new JudgeDecision(findings, Name, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds, null, raw);
    }

    /// <summary>JSON Schema for the decision. Sent as response_format so llama.cpp constrains generation with a grammar.</summary>
    public const string Schema = """
        {"type":"object","properties":{"findings":{"type":"array","items":{"type":"object","properties":{
        "kind":{"type":"string","enum":["remember","check","resolve","answer","organize","research","improve"]},
        "confidence":{"type":"number"},
        "summary":{"type":"string"},
        "why":{"type":"string"},
        "focused_prompt":{"type":"string"},
        "segment_ids":{"type":"array","items":{"type":"string"}},
        "topic":{"type":["string","null"]},
        "project":{"type":["string","null"]},
        "presentation":{"type":["string","null"],"enum":["none","ambient","result","alert","proposal","findings",null]},
        "note_text":{"type":["string","null"]},
        "note_type":{"type":["string","null"]},
        "merge_key":{"type":["string","null"]}},
        "required":["kind","confidence","summary","why","focused_prompt","segment_ids"]}}},"required":["findings"]}
        """;

    public static string SystemPrompt(JudgeContext context)
    {
        var sb = new StringBuilder();
        sb.Append("You are RELAY0's judge inside Relay, a local-first personal orchestrator. You listen to a rolling window of what the user and others say and decide whether anything in the NEW segments deserves work. ");
        sb.Append("You never act; you name tasks for deterministic software to run under the user's policy. Be selective: most windows contain nothing worth a task. Return an empty findings array then.\n\n");
        sb.Append("Task kinds: remember (a decision, commitment, constraint, correction or reusable idea worth keeping as a note), check (a stated fact about a known project that should be compared with stored decisions), ");
        sb.Append("resolve (an acronym, name or reference whose meaning should be available at once), answer (a direct question to Relay), organize (a structural change to projects or notes), research (a bounded external investigation), improve (a change to Relay's own preferences or behaviour).\n\n");
        sb.Append("Rules: summary at most 12 words; why at most 10 words and a category (\"decision about a deadline\", \"unfamiliar acronym\"), never quoting what was said, because it is written to the audit ledger while the words are not; focused_prompt is the exact question a planner with read-only tools should work on, quoting the words that triggered it; ");
        sb.Append("segment_ids must list only the ids of the segments that substantiate the finding, usually one or two; note_text for remember findings is the note as it should be filed, one sentence, no fluff; ");
        sb.Append("project is the name of a listed active project when the words clearly concern it, otherwise null; presentation is a suggestion only (none, ambient, result, alert, proposal, findings). ");
        sb.Append("Ordinary chatter, greetings, filler and things already handled are not findings. Watched terms always produce a resolve finding when mentioned.\n\n");
        sb.Append("Reply with exactly one JSON object: {\"findings\":[...]} and nothing else.");
        if (!string.IsNullOrWhiteSpace(context.PreferenceFragment)) sb.Append("\n\nUser preferences: ").Append(context.PreferenceFragment);
        return sb.ToString();
    }

    private static string UserPrompt(JudgeRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("Time: ").Append(request.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\n");
        sb.Append("Active projects: ").Append(request.Context.ActiveProjects.Count == 0 ? "none" : string.Join("; ", request.Context.ActiveProjects)).Append('\n');
        if (request.Context.WatchedTerms.Count > 0) sb.Append("Watched terms (always resolve): ").Append(string.Join(", ", request.Context.WatchedTerms)).Append('\n');
        if (request.Context.RecentTopics.Count > 0) sb.Append("Recent topics: ").Append(string.Join(", ", request.Context.RecentTopics)).Append('\n');
        if (request.Origin == TaskOrigin.Direct)
        {
            sb.Append("Origin: direct ask. Classify it (one finding) and write the focused prompt.\nInstruction: ").Append(request.DirectText);
            return sb.ToString();
        }
        sb.Append("Origin: observed stream. Window (oldest first; NEW marks segments not yet judged):\n");
        foreach (var s in request.Window)
        {
            var isNew = request.NewSegmentIds.Contains(s.SegmentId, StringComparer.Ordinal);
            sb.Append(isNew ? "NEW " : "    ").Append('[').Append(s.SegmentId).Append(' ').Append(s.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (s.Speaker is not null) sb.Append(' ').Append(s.Speaker);
            sb.Append("] ").Append(s.Text.Replace('\n', ' ')).Append('\n');
        }
        return sb.ToString();
    }

    private static string? Blank(JsonNode? node)
    {
        var s = node?.GetValue<string>();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
