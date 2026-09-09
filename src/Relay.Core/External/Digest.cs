using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Model;

namespace Relay.Core.External;

/// <summary>What the digester produced: the lines, or why there are none. Never throws; a reply without a digest is still a reply.</summary>
public sealed record DigestResult(IReadOnlyList<string> Lines, string? Error, string? Digester, int PromptTokens, int CompletionTokens, long ElapsedMs)
{
    public static readonly DigestResult None = new([], null, null, 0, 0, 0);
    public bool Ok => Lines.Count > 0;
}

/// <summary>
/// The second utility prompt of docs/09 (<c>digest.md</c>): the local model turns an external AI's reply into
/// at most three feed lines for the user. The reply itself is kept whole as the artifact; the digest is what
/// the feed shows and what the mind reads first. Replaceable by <c>config\prompts\digest.md</c>. Short replies
/// are not sent to the model at all: their own first lines are the digest.
/// </summary>
public static class Digest
{
    public const string PromptName = "digest";
    public const string SchemaName = "digest";
    public const int MaxLines = 3;
    public const int MaxLineChars = 160;
    /// <summary>Replies shorter than this are digested deterministically (first lines); the model is asked only for longer ones.</summary>
    public const int MinCharsForModel = 400;
    public const int MaxReplyChars = 12_000;
    public const int MaxOutputTokens = 300;

    public const string DefaultInstructions =
        "You condense another AI's reply for a small activity feed. Write at most three lines, each under 160 characters, plain prose, present tense, " +
        "no markdown, no preamble. Line 1: the answer or main finding. Line 2: the strongest supporting point or caveat. Line 3 (only if needed): what the reply could not settle. " +
        "Keep the reply's own numbers, names and dates exactly; never add facts it does not contain. If the reply says it could not answer, say that in one line.";

    public const string Schema = """
        {"type":"object","properties":{"lines":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":3}},"required":["lines"],"additionalProperties":false}
        """;

    public static string System(string? instructionsOverride)
        => string.IsNullOrWhiteSpace(instructionsOverride) ? DefaultInstructions : instructionsOverride!.Trim();

    public static string User(string objective, string reply)
    {
        var text = reply.Length > MaxReplyChars ? reply[..MaxReplyChars] + "\n…(the reply continues; digest what is here)" : reply;
        return "The reply answered this objective:\n" + (objective.Length > 600 ? objective[..600] + "…" : objective) + "\n\nThe reply:\n" + text + "\n\nReply with one JSON object: {\"lines\": [\"…\"]}.";
    }

    /// <summary>Digests without a model: the first non-empty lines of the reply, clipped. Used for short replies and when the model is off or fails.</summary>
    public static IReadOnlyList<string> Plain(string reply)
    {
        var lines = new List<string>();
        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', ' ');
            if (line.Length == 0) continue;
            lines.Add(line.Length > MaxLineChars ? line[..(MaxLineChars - 1)] + "…" : line);
            if (lines.Count == MaxLines) break;
        }
        return lines;
    }

    /// <summary>
    /// The digest of one reply. <paramref name="digester"/> is the local model (null when it is off); the fixed prompt, or
    /// its override, asks for the lines under a schema. Falls back to <see cref="Plain"/> so the feed always has something.
    /// </summary>
    public static async Task<DigestResult> MakeAsync(IModelClient? digester, string? instructionsOverride, string objective, string reply, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reply)) return DigestResult.None;
        if (digester is null || reply.Length < MinCharsForModel) return new DigestResult(Plain(reply), null, null, 0, 0, 0);
        var watch = Stopwatch.StartNew();
        ModelResponse response;
        try
        {
            var messages = new List<ModelMessage> { new("system", System(instructionsOverride)), new("user", User(objective, reply)) };
            response = await digester.CompleteAsync(new ModelRequest(digester.Model, messages, MaxOutputTokens, JsonObject: true, JsonSchema: Schema, SchemaName: SchemaName), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TimeoutException)
        {
            return new DigestResult(Plain(reply), $"{digester.Model}: {ex.Message}", null, 0, 0, watch.ElapsedMilliseconds);
        }
        // Fallbacks carry no digester name: the lines are the reply's own, and the error says what the model did not do.
        if (!response.Ok) return new DigestResult(Plain(reply), $"{digester.Model}: {response.Error ?? "digester failed"}", null, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds);
        var lines = Parse(response.Content ?? "");
        return lines.Count > 0
            ? new DigestResult(lines, null, digester.Model, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds)
            : new DigestResult(Plain(reply), $"{digester.Model}: the digester did not return lines", null, response.PromptTokens, response.CompletionTokens, watch.ElapsedMilliseconds);
    }

    /// <summary>Reads the model's JSON; tolerant of a bare array or lines joined in one string.</summary>
    public static IReadOnlyList<string> Parse(string raw)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { return []; }
        var array = node switch
        {
            JsonObject o => o["lines"] as JsonArray,
            JsonArray a => a,
            _ => null,
        };
        if (array is null) return [];
        var lines = new List<string>();
        foreach (var item in array)
        {
            var text = (item?.ToString() ?? "").Trim();
            if (text.Length == 0) continue;
            foreach (var part in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                lines.Add(part.Length > MaxLineChars ? part[..(MaxLineChars - 1)] + "…" : part);
                if (lines.Count == MaxLines) return lines;
            }
        }
        return lines;
    }
}
