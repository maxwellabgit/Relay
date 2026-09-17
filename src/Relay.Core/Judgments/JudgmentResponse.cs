using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

public static class JudgmentErrorCodes
{
    public const string ProviderContractError = "provider_contract_error";
    public const string AuthBlocked = "auth_blocked";
    public const string InvalidContract = "invalid_contract";
    public const string Throttled = "throttled";
    public const string Overload = "overload";
    public const string Transient = "transient";
    public const string Cancelled = "cancelled";
    public const string CircuitOpen = "circuit_open";
    public const string MissingApiKey = "missing_api_key";
    public const string FixtureMiss = "fixture_miss";
}

/// <summary>Choice answer — selected option, full distribution, confidence. Not used by Noul.</summary>
public sealed class ChoiceAnswer
{
    [JsonPropertyName("choice")] public required string Choice { get; init; }
    [JsonPropertyName("probabilities")] public required Dictionary<string, double> Probabilities { get; init; }
    [JsonPropertyName("confidence")] public required double Confidence { get; init; }
}

/// <summary>Score answer — fractional score allowed; distribution over levels; confidence.</summary>
public sealed class ScoreAnswer
{
    [JsonPropertyName("score")] public required double Score { get; init; }
    [JsonPropertyName("legend")] public Dictionary<string, string>? Legend { get; init; }
    [JsonPropertyName("probabilities")] public required Dictionary<string, double> Probabilities { get; init; }
    [JsonPropertyName("confidence")] public required double Confidence { get; init; }
}

/// <summary>Noul answer — P(yes) only. No Choice/Score confidence property.</summary>
public sealed class NoulAnswer
{
    [JsonPropertyName("noul")] public required double Noul { get; init; }
}

public sealed class JudgmentUsage
{
    [JsonPropertyName("input_tokens")] public long InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public long OutputTokens { get; init; }
}

/// <summary>Validated System One response. Malformed responses never invent answers.</summary>
public sealed class JudgmentResponse
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("answers")] public required Dictionary<string, JsonElement> Answers { get; init; }
    [JsonPropertyName("usage")] public JudgmentUsage? Usage { get; init; }

    [JsonIgnore] public bool Ok { get; init; } = true;
    [JsonIgnore] public string? ErrorCode { get; init; }
    [JsonIgnore] public string? Error { get; init; }
    [JsonIgnore] public int? HttpStatus { get; init; }
    [JsonIgnore] public bool Retryable { get; init; }

    public static JudgmentResponse Fail(string errorCode, string error, int? httpStatus = null, bool retryable = false)
        => new()
        {
            Model = "",
            Answers = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Ok = false,
            ErrorCode = errorCode,
            Error = error,
            HttpStatus = httpStatus,
            Retryable = retryable,
        };

    public ChoiceAnswer? TryGetChoice(string key)
    {
        if (!Answers.TryGetValue(key, out var el)) return null;
        return JsonSerializer.Deserialize<ChoiceAnswer>(el.GetRawText(), JudgmentJson.Options);
    }

    public ScoreAnswer? TryGetScore(string key)
    {
        if (!Answers.TryGetValue(key, out var el)) return null;
        return JsonSerializer.Deserialize<ScoreAnswer>(el.GetRawText(), JudgmentJson.Options);
    }

    public NoulAnswer? TryGetNoul(string key)
    {
        if (!Answers.TryGetValue(key, out var el)) return null;
        return JsonSerializer.Deserialize<NoulAnswer>(el.GetRawText(), JudgmentJson.Options);
    }
}

/// <summary>Validates a provider response against the request contract. Failures → provider_contract_error.</summary>
public static class JudgmentResponseValidator
{
    public static JudgmentResponse Validate(JudgmentRequest request, string responseJson, string? reportedModel = null, int? httpStatus = null)
    {
        JudgmentResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JudgmentResponse>(responseJson, JudgmentJson.Options);
        }
        catch (JsonException ex)
        {
            return JudgmentResponse.Fail(JudgmentErrorCodes.ProviderContractError, "Malformed JSON: " + ex.Message, httpStatus);
        }

        if (parsed is null)
            return JudgmentResponse.Fail(JudgmentErrorCodes.ProviderContractError, "Empty response body.", httpStatus);

        if (string.IsNullOrWhiteSpace(parsed.Model) && string.IsNullOrWhiteSpace(reportedModel))
            return JudgmentResponse.Fail(JudgmentErrorCodes.ProviderContractError, "Missing required model field.", httpStatus);

        if (parsed.Usage is null)
            return JudgmentResponse.Fail(JudgmentErrorCodes.ProviderContractError, "Missing required usage fields.", httpStatus);

        var model = string.IsNullOrWhiteSpace(parsed.Model) ? reportedModel! : parsed.Model;
        var answers = parsed.Answers ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // Exactly the requested answer keys.
        if (answers.Count != request.Questions.Count
            || request.Questions.Keys.Any(k => !answers.ContainsKey(k))
            || answers.Keys.Any(k => !request.Questions.ContainsKey(k)))
        {
            return JudgmentResponse.Fail(JudgmentErrorCodes.ProviderContractError,
                "Answer keys must match the requested question keys exactly.", httpStatus);
        }

        foreach (var (key, question) in request.Questions)
        {
            var answer = answers[key];
            var err = question.Type switch
            {
                JudgmentQuestionTypes.Choice => ValidateChoice(question, answer),
                JudgmentQuestionTypes.Score => ValidateScore(question, answer),
                JudgmentQuestionTypes.Noul => ValidateNoul(answer),
                _ => $"Unknown question type '{question.Type}'.",
            };
            if (err is not null)
                return JudgmentResponse.Fail(JudgmentErrorCodes.ProviderContractError, $"Question '{key}': {err}", httpStatus);
        }

        return new JudgmentResponse
        {
            Model = model,
            Answers = answers,
            Usage = parsed.Usage,
            Ok = true,
            HttpStatus = httpStatus,
        };
    }

    private static string? ValidateChoice(JudgmentQuestion question, JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) return "Choice answer must be an object.";
        if (!answer.TryGetProperty("choice", out var choiceEl) || choiceEl.ValueKind != JsonValueKind.String)
            return "Missing choice string.";
        if (!answer.TryGetProperty("probabilities", out var probs) || probs.ValueKind != JsonValueKind.Object)
            return "Missing probabilities object.";
        if (!answer.TryGetProperty("confidence", out var confEl) || !TryFinite01(confEl, out _))
            return "Missing or invalid confidence in [0,1].";

        var options = ReadChoiceOptions(question.Criteria);
        if (options.Count == 0) return "Choice criteria options missing.";

        var choice = choiceEl.GetString()!;
        if (!options.Contains(choice))
            return $"Choice '{choice}' is not in the option set.";

        var keys = new HashSet<string>(StringComparer.Ordinal);
        double sum = 0;
        foreach (var p in probs.EnumerateObject())
        {
            keys.Add(p.Name);
            if (!TryFinite01(p.Value, out var v)) return $"Probability for '{p.Name}' is not finite in [0,1].";
            sum += v;
        }
        if (!keys.SetEquals(options))
            return "Probability keys must match the Choice options exactly.";
        if (Math.Abs(sum - 1.0) > JudgmentDefaults.DistributionSumTolerance)
            return $"Probabilities sum to {sum.ToString(CultureInfo.InvariantCulture)}, expected ~1 (±{JudgmentDefaults.DistributionSumTolerance}).";
        return null;
    }

    private static string? ValidateScore(JudgmentQuestion question, JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) return "Score answer must be an object.";
        if (!answer.TryGetProperty("score", out var scoreEl) || !TryFinite(scoreEl, out var score))
            return "Missing or non-finite score.";
        if (!answer.TryGetProperty("probabilities", out var probs) || probs.ValueKind != JsonValueKind.Object)
            return "Missing probabilities object.";
        if (!answer.TryGetProperty("confidence", out var confEl) || !TryFinite01(confEl, out _))
            return "Missing or invalid confidence in [0,1].";

        var levels = ReadScoreLevels(question.Criteria);
        if (levels.Count < 2) return "Score criteria must define at least two levels.";

        var maxIndex = levels.Count - 1;
        if (score < 0 || score > maxIndex)
            return $"Score {score} outside rubric [0, {maxIndex}] (fractional scores allowed within range).";

        var expectedKeys = Enumerable.Range(0, levels.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        // Also accept level text keys if provider uses them.
        var levelTextKeys = levels.Select((l, i) => l).ToHashSet(StringComparer.Ordinal);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        double sum = 0;
        foreach (var p in probs.EnumerateObject())
        {
            keys.Add(p.Name);
            if (!TryFinite01(p.Value, out var v)) return $"Probability for '{p.Name}' is not finite in [0,1].";
            sum += v;
        }

        if (!keys.SetEquals(expectedKeys) && !keys.SetEquals(levelTextKeys))
            return "Probability keys must match Score levels (indices or level labels).";
        if (Math.Abs(sum - 1.0) > JudgmentDefaults.DistributionSumTolerance)
            return $"Probabilities sum to {sum.ToString(CultureInfo.InvariantCulture)}, expected ~1 (±{JudgmentDefaults.DistributionSumTolerance}).";
        return null;
    }

    private static string? ValidateNoul(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) return "Noul answer must be an object.";
        if (!answer.TryGetProperty("noul", out var noulEl) || !TryFinite01(noulEl, out _))
            return "Missing or invalid noul in [0,1].";
        // Noul must not carry Choice/Score confidence.
        if (answer.TryGetProperty("confidence", out _))
            return "Noul must not include a confidence property.";
        if (answer.TryGetProperty("choice", out _) || answer.TryGetProperty("score", out _))
            return "Noul must not include choice/score fields.";
        return null;
    }

    private static HashSet<string> ReadChoiceOptions(JsonElement? criteria)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (criteria is null || criteria.Value.ValueKind != JsonValueKind.Object) return set;
        foreach (var p in criteria.Value.EnumerateObject())
            set.Add(p.Name);
        return set;
    }

    private static List<string> ReadScoreLevels(JsonElement? criteria)
    {
        var list = new List<string>();
        if (criteria is null) return list;
        if (criteria.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in criteria.Value.EnumerateArray())
                list.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
        }
        return list;
    }

    private static bool TryFinite01(JsonElement el, out double value)
    {
        value = 0;
        if (!TryFinite(el, out value)) return false;
        return value >= 0 && value <= 1;
    }

    private static bool TryFinite(JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out value)) return false;
        return double.IsFinite(value);
    }
}

public static class JudgmentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
