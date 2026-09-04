using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Relay.Core.Notes;

public static class NoteTypes
{
    public const string Idea = "idea";
    public const string Fact = "fact";
    public const string Question = "question";
    public const string Decision = "decision";
    public const string Reference = "reference";
    public const string Task = "task";
    public const string RawCapture = "raw-capture";

    public static readonly string[] All = [Idea, Fact, Question, Decision, Reference, Task, RawCapture];

    /// <summary>Which project subfolder a note type is filed under.</summary>
    public static string Folder(string type) => type switch
    {
        Decision => "decisions",
        Task => "tasks",
        _ => "notes",
    };
}

public static class NoteStatus
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Disputed = "disputed";
    public const string Superseded = "superseded";
    public const string Archived = "archived";
}

/// <summary>
/// A canonical note file: a small, fixed YAML front matter block followed by the markdown body.
/// Every note carries project id, type, status, creation time, confidence, and the exact source
/// spans in the ledger that produced it (memory rules 1, 3, 7). The parser accepts only what the
/// writer produces, so a file edited outside Relay that no longer parses is reported, not guessed.
/// </summary>
public sealed class NoteDocument
{
    public required string Id { get; init; }
    public required string ProjectId { get; set; }
    public required string Type { get; set; }
    public string Status { get; set; } = NoteStatus.Active;
    public required DateTimeOffset Created { get; init; }
    public double? Confidence { get; set; }
    public string? CaptureId { get; init; }
    public string? Topic { get; set; }
    public List<SourceSpan> Spans { get; init; } = new();
    public List<string> Supersedes { get; init; } = new();
    public List<string> DisputedWith { get; init; } = new();
    public string Body { get; set; } = "";

    public string FileName => Id + ".md";
    public string RelativePath => Path.Combine(NoteTypes.Folder(Type), FileName);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("id: ").Append(Id).Append('\n');
        sb.Append("project: ").Append(ProjectId).Append('\n');
        sb.Append("type: ").Append(Type).Append('\n');
        sb.Append("status: ").Append(Status).Append('\n');
        sb.Append("created: ").Append(Created.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("confidence: ").Append(Confidence is { } c ? c.ToString("0.###", CultureInfo.InvariantCulture) : "null").Append('\n');
        sb.Append("capture: ").Append(CaptureId ?? "null").Append('\n');
        sb.Append("topic: ").Append(Topic is null ? "null" : Scalar(Topic)).Append('\n');
        sb.Append("spans:").Append(Spans.Count == 0 ? " []\n" : "\n");
        foreach (var s in Spans) sb.Append("  - { eventId: ").Append(s.EventId).Append(", start: ").Append(s.Start).Append(", end: ").Append(s.End).Append(" }\n");
        sb.Append("supersedes: [").Append(string.Join(", ", Supersedes)).Append("]\n");
        sb.Append("disputedWith: [").Append(string.Join(", ", DisputedWith)).Append("]\n");
        sb.Append("---\n");
        sb.Append(Body);
        if (!Body.EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    public string Sha256() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToMarkdown())));

    private static string Scalar(string value)
    {
        var needsQuotes = value.Length == 0 || value.Any(ch => ch is ':' or '#' or '[' or ']' or '{' or '}' or ',' or '"' or '\n') || value.StartsWith(' ') || value.EndsWith(' ');
        return needsQuotes ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"" : value;
    }

    public static NoteDocument Parse(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new FormatException("Note is missing front matter.");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("Note front matter is not terminated.");
        var header = text[4..end];
        var body = text[(end + 5)..];

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var spans = new List<SourceSpan>();
        string? currentList = null;
        foreach (var raw in header.Split('\n'))
        {
            if (raw.Length == 0) continue;
            if (raw.StartsWith("  - ", StringComparison.Ordinal))
            {
                if (currentList != "spans") throw new FormatException("Unexpected list item in front matter.");
                spans.Add(ParseSpan(raw[4..].Trim()));
                continue;
            }
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) throw new FormatException($"Malformed front matter line: {raw}");
            var key = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();
            currentList = key == "spans" && value.Length == 0 ? "spans" : null;
            fields[key] = value;
        }

        string Require(string key) => fields.TryGetValue(key, out var v) ? v : throw new FormatException($"Front matter is missing '{key}'.");
        static string? Nullable(string v) => v == "null" ? null : Unquote(v);

        var doc = new NoteDocument
        {
            Id = Require("id"),
            ProjectId = Require("project"),
            Type = Require("type"),
            Status = Require("status"),
            Created = DateTimeOffset.Parse(Require("created"), CultureInfo.InvariantCulture),
            Confidence = Nullable(Require("confidence")) is { } conf ? double.Parse(conf, CultureInfo.InvariantCulture) : null,
            CaptureId = Nullable(fields.GetValueOrDefault("capture", "null")),
            Topic = Nullable(fields.GetValueOrDefault("topic", "null")),
            Spans = spans,
            Supersedes = ParseFlowList(fields.GetValueOrDefault("supersedes", "[]")),
            DisputedWith = ParseFlowList(fields.GetValueOrDefault("disputedWith", "[]")),
            Body = body.TrimEnd('\n'),
        };
        if (fields.TryGetValue("spans", out var inline) && inline == "[]" && spans.Count > 0) throw new FormatException("Conflicting spans.");
        return doc;
    }

    private static SourceSpan ParseSpan(string text)
    {
        if (!text.StartsWith('{') || !text.EndsWith('}')) throw new FormatException($"Malformed span: {text}");
        var parts = text[1..^1].Split(',').Select(p => p.Split(':', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        return new SourceSpan(parts["eventId"], int.Parse(parts["start"], CultureInfo.InvariantCulture), int.Parse(parts["end"], CultureInfo.InvariantCulture));
    }

    private static List<string> ParseFlowList(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) throw new FormatException($"Malformed list: {text}");
        var inner = trimmed[1..^1].Trim();
        return inner.Length == 0 ? new List<string>() : inner.Split(',').Select(s => s.Trim()).ToList();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value[1..^1].Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return value;
    }
}
