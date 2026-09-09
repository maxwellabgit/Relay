using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Storage;

namespace Relay.Core.Usage;

/// <summary>
/// One line per finished task: how it was routed, what the mind read, what it cost, how it ended, and
/// what the user did with it. Metadata only — no words from the task — so the lines can be analysed
/// freely to tune the decision weights. Overrides (the user took a different path than the decider
/// chose) are the labels.
/// </summary>
public sealed record UsageLine(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("mind")] string Mind,
    [property: JsonPropertyName("route")] string? Route,
    [property: JsonPropertyName("complexity")] double? Complexity,
    [property: JsonPropertyName("needs")] IReadOnlyList<string> Needs,
    [property: JsonPropertyName("significance")] double? Significance,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("toolCalls")] int ToolCalls,
    [property: JsonPropertyName("proposals")] int Proposals,
    [property: JsonPropertyName("promptTokens")] int PromptTokens,
    [property: JsonPropertyName("completionTokens")] int CompletionTokens,
    [property: JsonPropertyName("wallMs")] long WallMs,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("decisions")] IReadOnlyDictionary<string, string> Decisions,
    [property: JsonPropertyName("userResponse")] string? UserResponse,
    [property: JsonPropertyName("override")] string? Override);

public sealed class UsageRecorder
{
    private readonly DataRoot _root;
    private readonly object _gate = new();

    public UsageRecorder(DataRoot root) => _root = root;

    public string PathFor(DateTimeOffset at) => Path.Combine(_root.UsageDirectory, at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary>Appends the line; returns the file it went to. A failure to write usage never fails the task.</summary>
    public string? Record(UsageLine line)
    {
        var path = PathFor(line.At);
        try
        {
            Directory.CreateDirectory(_root.UsageDirectory);
            var json = JsonSerializer.Serialize(line, RelayJson.Compact) + "\n";
            lock (_gate)
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<UsageLine> Read(DateTimeOffset day)
    {
        var path = PathFor(day);
        if (!File.Exists(path)) return [];
        var lines = new List<UsageLine>();
        foreach (var raw in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try { if (JsonSerializer.Deserialize<UsageLine>(raw, RelayJson.Compact) is { } line) lines.Add(line); }
            catch (JsonException) { /* a torn line is skipped, not fatal */ }
        }
        return lines;
    }
}
