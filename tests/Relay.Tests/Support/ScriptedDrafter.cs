using System.Text.Json;
using Relay.Core.Model;
using Relay.Core.Storage;

namespace Relay.Tests.Support;

/// <summary>
/// The model that drafts tools for the build move, scripted: each call takes the next reply. A reply is a
/// package (description, arguments, host functions, source, tests) serialised the way the build prompt
/// asks for it, or a raw string when a test wants the parser to choke. Records every request so a test
/// can read what the retry was told. <see cref="Block"/> makes the next call wait until it is cancelled,
/// for the stop path.
/// </summary>
public sealed class ScriptedDrafter : IModelClient
{
    private readonly Queue<Func<ModelRequest, CancellationToken, Task<string>>> _replies = new();

    public string Host => "127.0.0.1:11434";
    public string Model => "drafter:scripted";
    public List<ModelRequest> Requests { get; } = new();
    public int Calls => Requests.Count;

    public ScriptedDrafter Reply(string raw) { _replies.Enqueue((_, _) => Task.FromResult(raw)); return this; }

    public ScriptedDrafter Reply(object package) => Reply(JsonSerializer.Serialize(package, RelayJson.Compact));

    /// <summary>The next call never answers; it ends only when the caller's token is cancelled.</summary>
    public ScriptedDrafter Block()
    {
        _replies.Enqueue(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false); return ""; });
        return this;
    }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_replies.Count == 0) return ModelResponse.Failed("the scripted drafter has no reply left", 1);
        var content = await _replies.Dequeue()(request, cancellationToken).ConfigureAwait(false);
        return new ModelResponse(true, content, request.Messages.Sum(m => m.Content.Length) / 4, content.Length / 4, 5, null);
    }

    /// <summary>A world-clock package as the drafter would return it; <paramref name="source"/> lets a test break it.</summary>
    public static object WorldClock(string? source = null, string[]? keys = null) => new
    {
        description = "The current local time, date and weekday in an IANA time zone.",
        arguments = new[] { new { name = "zone", description = "IANA zone id such as Europe/London or Asia/Tokyo", required = true } },
        hostFunctions = new[] { "time.zone" },
        source = source ?? WorldClockSource,
        tests = new[] { new { args = new Dictionary<string, string> { ["zone"] = "Europe/London" }, keys = keys ?? ["zone", "time", "date"], contains = "Europe/London" } },
    };

    public const string WorldClockSource = """
        function run(args) {
          var zone = String(args.zone || "").trim();
          if (!zone) throw new Error("zone is required, e.g. Europe/London");
          var z = relay.zone(zone);
          return { zone: z.zone, time: z.local.time, date: z.local.date, weekday: z.local.weekday, offset: z.offset, utc: z.utc };
        }
        """;
}
