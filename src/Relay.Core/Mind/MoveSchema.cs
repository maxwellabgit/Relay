using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Relay.Core.Mind;

/// <summary>
/// The one contract the mind answers with on every step, and its parser. The schema is sent as
/// <c>response_format</c> so a llama.cpp host constrains generation with a grammar; the move is flat
/// (five fields, always present) so the same schema is small for the grammar and usable by hosts
/// that require every property. <see cref="Parse"/> is tolerant of what does not matter (a missing
/// read, a number as a string) and strict about what does (an unknown move type, a say without text).
/// </summary>
public static partial class MoveSchema
{
    public const string SchemaName = "mind_step";
    public const string ObservingSchemaName = "mind_observe";
    public const int MaxFeedChars = 200;
    public const int DefaultDelegateBudget = 2_000;
    /// <summary>The least a delegate may be given: a small local mind tends to write tiny budgets, and a research reply cut at 200 tokens is worthless.</summary>
    public const int MinDelegateBudget = 800;

    public const string Json = """
        {"type":"object","properties":{
        "read":{"type":"object","properties":{
        "intent":{"type":"string"},
        "complexity":{"type":"number"},
        "needs":{"type":"array","items":{"type":"string","enum":["none","local_notes","world_knowledge","new_tool","external_reasoning","user_input"]}},
        "significance":{"type":"number"},
        "sensitivity":{"type":"number"},
        "risk":{"type":"object","properties":{"core":{"type":"number"},"security":{"type":"number"},"loop":{"type":"number"},"destructive":{"type":"number"}},"required":["core","security","loop","destructive"],"additionalProperties":false}},
        "required":["intent","complexity","needs","significance","sensitivity","risk"],"additionalProperties":false},
        "move":{"type":"object","properties":{
        "type":{"type":"string","enum":["say","use_tool","propose","delegate","build","ask_user","wait","stop"]},
        "text":{"type":"string"},
        "name":{"type":"string"},
        "args":{"type":"object","additionalProperties":{"type":"string"}},
        "done":{"type":"boolean"}},
        "required":["type","text","name","args","done"],"additionalProperties":false},
        "feed":{"type":"string"}},
        "required":["read","move","feed"],"additionalProperties":false}
        """;

    /// <summary>
    /// The same shape for a listening pass, with the move enum cut to what the observing loop may do. The narrower
    /// enum is the point: a grammar that cannot emit <c>propose</c> or <c>delegate</c> spares the loop from refusing
    /// them and the user from a pass that spends its steps being corrected.
    /// </summary>
    public const string ObservingJson = """
        {"type":"object","properties":{
        "read":{"type":"object","properties":{
        "intent":{"type":"string"},
        "complexity":{"type":"number"},
        "needs":{"type":"array","items":{"type":"string","enum":["none","local_notes","world_knowledge","new_tool","external_reasoning","user_input"]}},
        "significance":{"type":"number"},
        "sensitivity":{"type":"number"},
        "risk":{"type":"object","properties":{"core":{"type":"number"},"security":{"type":"number"},"loop":{"type":"number"},"destructive":{"type":"number"}},"required":["core","security","loop","destructive"],"additionalProperties":false}},
        "required":["intent","complexity","needs","significance","sensitivity","risk"],"additionalProperties":false},
        "move":{"type":"object","properties":{
        "type":{"type":"string","enum":["wait","say","use_tool","raise"]},
        "text":{"type":"string"},
        "name":{"type":"string"},
        "args":{"type":"object","additionalProperties":{"type":"string"}},
        "done":{"type":"boolean"}},
        "required":["type","text","name","args","done"],"additionalProperties":false},
        "feed":{"type":"string"}},
        "required":["read","move","feed"],"additionalProperties":false}
        """;

    /// <summary>The kinds of work a raise may name; anything else is read as the nearest sensible one.</summary>
    public static readonly string[] RaiseKinds = ["remember", "check", "resolve", "answer", "organize", "research", "improve"];

    [GeneratedRegex("[^a-z0-9_]+", RegexOptions.CultureInvariant)] private static partial Regex NotIdentifier();
    /// <summary>The digits of a window line's label, however the mind wrote it: <c>#3</c>, <c>3</c>, <c>[#3]</c>, <c>s3</c>, <c>segment 3</c>.</summary>
    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)] private static partial Regex Digits();
    /// <summary>A schema placeholder copied into a value ("&lt;ids returned by tools&gt;").</summary>
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)] private static partial Regex Placeholder();

    /// <summary>Turns the model's reply into a step. Throws <see cref="FormatException"/> with a message the mind can act on when the contract is broken.</summary>
    public static MindStep Parse(string raw, int promptChars = 0, long elapsedMs = 0, int promptTokens = 0, int completionTokens = 0)
    {
        JsonObject root;
        try { root = JsonNode.Parse(raw)?.AsObject() ?? throw new FormatException("the reply was not a JSON object"); }
        catch (JsonException ex) { throw new FormatException("the reply was not valid JSON: " + ex.Message); }
        catch (InvalidOperationException) { throw new FormatException("the reply was not a JSON object"); }

        // A small model sometimes answers with the move at the top level; accept it.
        var moveNode = root["move"] as JsonObject ?? (root["type"] is not null ? root : null) ?? throw new FormatException("the reply had no 'move' object");
        var move = ParseMove(moveNode);
        var read = root["read"] is JsonObject r ? ParseRead(r) : null;
        var feed = Str(root["feed"]).Trim();
        if (feed.Length == 0) feed = move.Brief();
        if (feed.Length > MaxFeedChars) feed = feed[..(MaxFeedChars - 1)] + "…";
        return new MindStep(read, move, feed, raw, promptChars, promptTokens, completionTokens, elapsedMs, null);
    }

    /// <summary>A tool is named as listed; a model that copies the listing ("world_clock(zone)"), the case or a hyphen is still understood.</summary>
    public static string ToolName(string name)
    {
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];
        return name.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
    }

    /// <summary>A refs token that stands for "no reference": a placeholder copied from the schema, or a word for nothing.</summary>
    public static bool IsNoRef(string token)
    {
        var t = token.Trim().Trim('"', '\'', '[', ']');
        if (t.Length == 0 || t.Contains('<') || t.Contains('>')) return true;
        return t.ToLowerInvariant() is "none" or "null" or "nil" or "n/a" or "na" or "no" or "nothing" or "empty" or "-" or "--" or "undefined" or "refs" or "ids" or "id";
    }

    /// <summary>A window line's label as the transcript showed it, or empty when the token names no line.</summary>
    public static string SegmentLabel(string token)
    {
        if (IsNoRef(token)) return "";
        var digits = Digits().Match(token);
        return digits.Success ? "#" + digits.Value.TrimStart('0').PadLeft(1, '0') : "";
    }

    /// <summary>The kind of work a raise names. An unrecognised word with note text is a thing to remember; without it, a thing to answer.</summary>
    public static string RaiseKind(string name, bool hasNote)
    {
        var kind = name.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (RaiseKinds.Contains(kind, StringComparer.Ordinal)) return kind;
        return kind switch
        {
            "note" or "fact" or "decision" or "remember_this" or "keep" or "record" or "memory" => "remember",
            "question" or "open_question" or "unresolved" or "conflict" or "dispute" or "correction" => "resolve",
            "todo" or "task" or "commitment" or "action" or "follow_up" or "followup" or "reminder" => "check",
            "look_up" or "lookup" or "find_out" or "investigate" => "research",
            "file" or "sort" or "tidy" => "organize",
            _ => hasNote ? "remember" : "answer",
        };
    }

    public static Move ParseMove(JsonObject m)
    {
        var type = Str(m["type"]).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (!Move.Types.Contains(type)) throw new FormatException($"unknown move type '{type}'; allowed: {string.Join(", ", Move.Types)}");
        var text = Str(m["text"] ?? m["reason"] ?? m["prompt"] ?? m["question"] ?? m["justification"]).Trim();
        var name = Str(m["name"] ?? m["tool"] ?? m["action"] ?? m["profile"]).Trim();
        var args = Args(m["args"] as JsonObject ?? m["target"] as JsonObject);
        var done = m["done"] is JsonValue dv && dv.TryGetValue<bool>(out var d) ? d : string.Equals(Str(m["done"]), "true", StringComparison.OrdinalIgnoreCase);

        switch (type)
        {
            case Move.Say:
                if (text.Length == 0) throw new FormatException("a say move needs text");
                return new SayMove(text, done);
            case Move.UseTool:
                if (name.Length == 0) throw new FormatException("a use_tool move needs the tool's name in 'name'");
                return new UseToolMove(ToolName(name), args);
            case Move.Propose:
                if (name.Length == 0) throw new FormatException("a propose move needs the action in 'name'");
                return new ProposeMove(name, args, text.Length == 0 ? "Proposed by the mind." : text);
            case Move.Delegate:
                var replyTo = (args.GetValueOrDefault("reply_to") ?? args.GetValueOrDefault("continue") ?? args.GetValueOrDefault("request") ?? "").Trim();
                if (name.Length == 0 && replyTo.Length == 0) throw new FormatException("a delegate move needs the profile in 'name'");
                if (text.Length == 0) throw new FormatException("a delegate move needs the prompt you wrote in 'text'");
                // Refs are ids returned by tools. Small models write the schema's placeholder or a word for "nothing" ("none", "n/a", "<ids>"): those are no refs at all.
                var refs = Placeholder().Replace(args.GetValueOrDefault("refs") ?? "", " ").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(r => !IsNoRef(r)).Distinct(StringComparer.Ordinal).ToList();
                var budget = int.TryParse(args.GetValueOrDefault("budget_tokens") ?? args.GetValueOrDefault("budget"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? Math.Clamp(b, MinDelegateBudget, 32_000) : DefaultDelegateBudget;
                var search = string.Equals(args.GetValueOrDefault("allow_search"), "true", StringComparison.OrdinalIgnoreCase);
                return new DelegateMove(name, text, refs, budget, search, replyTo.Length == 0 ? null : replyTo);
            case Move.Build:
                var tool = NotIdentifier().Replace(name.ToLowerInvariant().Replace(' ', '_').Replace('-', '_'), "").Trim('_');
                if (tool.Length == 0) throw new FormatException("a build move needs a snake_case tool name in 'name'");
                if (tool.Length > 40) tool = tool[..40].TrimEnd('_');
                if (!char.IsAsciiLetter(tool[0])) tool = "t_" + tool;
                return new BuildMove(tool, text, args.GetValueOrDefault("inputs") ?? "", args.GetValueOrDefault("outputs") ?? "");
            case Move.AskUser:
                if (text.Length == 0) throw new FormatException("an ask_user move needs the question in 'text'");
                var options = (args.GetValueOrDefault("options") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                return new AskUserMove(text, options);
            case Move.Raise:
                if (text.Length == 0) throw new FormatException("a raise move needs the objective — what Relay should do about what it heard — in 'text'");
                var note = Trimmed(args, "note") ?? Trimmed(args, "note_text") ?? Trimmed(args, "text");
                var labels = Placeholder().Replace(args.GetValueOrDefault("segments") ?? args.GetValueOrDefault("segment") ?? args.GetValueOrDefault("lines") ?? "", " ")
                    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(SegmentLabel).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                return new RaiseMove(RaiseKind(name.Length > 0 ? name : args.GetValueOrDefault("kind") ?? "", note is not null), text, labels,
                    Trimmed(args, "why") ?? "", note, Trimmed(args, "note_type") ?? Trimmed(args, "type"), Trimmed(args, "project") ?? Trimmed(args, "project_id"),
                    Trimmed(args, "topic"), Trimmed(args, "merge_key"));
            case Move.Wait:
                return new WaitMove(text);
            default:
                return new StopMove(text);
        }
    }

    public static MindRead ParseRead(JsonObject r)
    {
        var needs = new List<string>();
        foreach (var n in r["needs"]?.AsArray() ?? [])
        {
            var need = Str(n).Trim().ToLowerInvariant();
            if (MindRead.KnownNeeds.Contains(need) && !needs.Contains(need)) needs.Add(need);
        }
        if (needs.Count == 0) needs.Add(MindRead.NeedNone);
        var risk = r["risk"] as JsonObject;
        return new MindRead(
            Clip(Str(r["intent"]).Trim(), 200),
            Unit(r["complexity"]),
            needs,
            Unit(r["significance"]),
            Unit(r["sensitivity"]),
            risk is null ? RiskRead.None : new RiskRead(Unit(risk["core"]), Unit(risk["security"]), Unit(risk["loop"]), Unit(risk["destructive"])));
    }

    /// <summary>An argument that carries something, or null: an empty string, a placeholder and a word for nothing are all nothing.</summary>
    private static string? Trimmed(IReadOnlyDictionary<string, string> args, string key)
    {
        var value = (args.GetValueOrDefault(key) ?? "").Trim();
        if (value.Length == 0 || Placeholder().Replace(value, "").Trim().Length == 0) return null;
        return IsNoRef(value) ? null : value;
    }

    private static IReadOnlyDictionary<string, string> Args(JsonObject? o)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o is null) return args;
        foreach (var (k, v) in o)
        {
            if (v is null) continue;
            args[k] = v is JsonValue value ? value.ToString() : v.ToJsonString();
        }
        return args;
    }

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.TryGetValue<string>(out var s) ? s : v.ToString(),
        _ => node.ToJsonString(),
    };

    /// <summary>A number in [0,1]; strings that parse are accepted; anything else is 0.</summary>
    private static double Unit(JsonNode? node)
    {
        double d = 0;
        if (node is JsonValue v)
        {
            if (!v.TryGetValue(out d) && v.TryGetValue<string>(out var s)) double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
        if (double.IsNaN(d)) return 0;
        return Math.Clamp(d, 0, 1);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
