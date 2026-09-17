using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Judgments;

/// <summary>
/// Exact request-hash → response matching. Unknown fixture requests FAIL LOUDLY.
/// Does not recognize product keywords or manufacture success.
/// </summary>
public sealed class FixtureJudgmentClient : IJudgmentClient
{
    private readonly Dictionary<string, string> _fixtures = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string Name => "fixture-judgment";

    public void Register(JudgmentRequest request, string responseJson)
    {
        var hash = CanonicalRequestHash(request);
        lock (_gate) _fixtures[hash] = responseJson;
    }

    public void RegisterRaw(string canonicalRequestJson, string responseJson)
    {
        var hash = HashBytes(Encoding.UTF8.GetBytes(canonicalRequestJson));
        lock (_gate) _fixtures[hash] = responseJson;
    }

    public Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = CanonicalRequestHash(request);
        string? body;
        lock (_gate) _fixtures.TryGetValue(hash, out body);
        if (body is null)
        {
            throw new InvalidOperationException(
                $"FixtureJudgmentClient: no fixture for request hash {hash}. " +
                "Unknown fixture requests fail loudly — register an exact request/response pair.");
        }

        var validated = JudgmentResponseValidator.Validate(request, body);
        return Task.FromResult(validated);
    }

    public static string CanonicalRequestHash(JudgmentRequest request)
    {
        var json = CanonicalRequestJson(request);
        return HashBytes(Encoding.UTF8.GetBytes(json));
    }

    public static string CanonicalRequestJson(JudgmentRequest request)
    {
        // Deterministic: sorted question keys, compact JSON.
        var questions = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, q) in request.Questions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            questions[k] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = q.Type,
                ["instructions"] = q.Instructions,
                ["criteria"] = q.Criteria.HasValue
                    ? JsonSerializer.Deserialize<object>(q.Criteria.Value.GetRawText(), RelayJson.Compact)
                    : null,
            };
        }

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["state"] = JsonSerializer.Deserialize<object>(request.State.GetRawText(), RelayJson.Compact),
            ["questions"] = questions,
        };
        return JsonSerializer.Serialize(payload, RelayJson.Compact);
    }

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>Replays recorded real request/response pairs by canonical request hash.</summary>
public sealed class ReplayJudgmentClient : IJudgmentClient
{
    private readonly FixtureJudgmentClient _inner = new();

    public string Name => "replay-judgment";

    public void AddRecording(JudgmentRequest request, string responseJson) => _inner.Register(request, responseJson);

    public void LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var reqJson = root.GetProperty("request").GetRawText();
            var resJson = root.GetProperty("response").GetRawText();
            var request = JsonSerializer.Deserialize<JudgmentRequest>(reqJson, JudgmentJson.Options)
                ?? throw new InvalidOperationException("Invalid replay request in " + file);
            _inner.Register(request, resJson);
        }
    }

    public Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken = default)
        => _inner.JudgeAsync(request, cancellationToken);
}
