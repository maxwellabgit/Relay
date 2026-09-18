using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Judgments;

/// <summary>Typed judgment response: either success payload or categorized failure.</summary>
public sealed class JudgmentResponse
{
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    [JsonPropertyName("success")] public JudgmentSuccess? Success { get; init; }
    [JsonPropertyName("failure")] public JudgmentFailure? Failure { get; init; }

    public static JudgmentResponse FromSuccess(JudgmentSuccess success) => new()
    {
        Ok = true,
        Success = success ?? throw new ArgumentNullException(nameof(success)),
    };

    public static JudgmentResponse FromFailure(JudgmentFailure failure) => new()
    {
        Ok = false,
        Failure = failure ?? throw new ArgumentNullException(nameof(failure)),
    };

    public void Validate()
    {
        if (Ok)
        {
            if (Success is null)
                throw new JudgmentValidationException("Successful response requires success payload.");
            if (Failure is not null)
                throw new JudgmentValidationException("Successful response must not include failure.");
            Success.Validate();
            return;
        }

        if (Failure is null)
            throw new JudgmentValidationException("Failed response requires failure payload.");
        if (Success is not null)
            throw new JudgmentValidationException("Failed response must not include success.");
        Failure.Validate();
    }

    public void ValidateAgainst(JudgmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate();
        if (Ok)
            Success!.ValidateAgainst(request);
    }
}

public sealed class JudgmentSuccess
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("answers")] public required IReadOnlyDictionary<string, JudgmentAnswer> Answers { get; init; }
    [JsonPropertyName("inputTokens")] public int InputTokens { get; init; }
    [JsonPropertyName("outputTokens")] public int OutputTokens { get; init; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; init; }
    [JsonPropertyName("providerRequestId")] public string? ProviderRequestId { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model))
            throw new JudgmentValidationException("model is required.");
        if (Answers is null || Answers.Count == 0)
            throw new JudgmentValidationException("answers must not be empty.");
        if (InputTokens < 0 || OutputTokens < 0)
            throw new JudgmentValidationException("token counts must be non-negative.");
        if (ElapsedMs < 0)
            throw new JudgmentValidationException("elapsedMs must be non-negative.");

        foreach (var (id, answer) in Answers)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new JudgmentValidationException("answer id must not be empty.");
            answer.Validate(id);
        }
    }

    public void ValidateAgainst(JudgmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate();
        foreach (var (id, question) in request.Questions)
        {
            if (!Answers.TryGetValue(id, out var answer))
                throw new JudgmentValidationException($"Answer for question '{id}' is missing.");
            switch (question, answer)
            {
                case (NoulQuestion, NoulAnswer):
                    break;
                case (ChoiceQuestion q, ChoiceAnswer a):
                    a.ValidateAgainstCriteria(id, q.Criteria);
                    break;
                case (ScoreQuestion q, ScoreAnswer a):
                    a.ValidateAgainstLevels(id, q.Criteria);
                    break;
                default:
                    throw new JudgmentValidationException($"Answer for '{id}' has the wrong type for its question.");
            }
        }

        foreach (var id in Answers.Keys)
        {
            if (!request.Questions.ContainsKey(id))
                throw new JudgmentValidationException($"Answer '{id}' does not match any asked question.");
        }
    }
}

public sealed class JudgmentFailure
{
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("httpStatus")] public int? HttpStatus { get; init; }

    /// <summary>Derived from category — authentication/validation are never retryable.</summary>
    [JsonPropertyName("retryable")]
    public bool Retryable => JudgmentFailureCategories.IsRetryable(Category);

    public void Validate()
    {
        if (!JudgmentFailureCategories.IsKnown(Category))
            throw new JudgmentValidationException($"Unknown failure category '{Category}'.");
        if (string.IsNullOrWhiteSpace(Message))
            throw new JudgmentValidationException("failure message is required.");
    }

    public static JudgmentFailure Create(string category, string message, int? httpStatus = null)
    {
        if (!JudgmentFailureCategories.IsKnown(category))
            throw new JudgmentValidationException($"Unknown failure category '{category}'.");
        if (string.IsNullOrWhiteSpace(message))
            throw new JudgmentValidationException("failure message is required.");

        return new JudgmentFailure
        {
            Category = category,
            Message = message,
            HttpStatus = httpStatus,
        };
    }
}

public static class JudgmentFailureCategories
{
    public const string Disabled = "disabled";
    public const string NotAuthorized = "not_authorized";
    public const string MissingSecret = "missing_secret";
    public const string Timeout = "timeout";
    public const string RateLimited = "rate_limited";
    public const string Overloaded = "overloaded";
    public const string Authentication = "authentication";
    public const string Validation = "validation";
    public const string InvalidResponse = "invalid_response";
    public const string Network = "network";
    /// <summary>Client-local cancellation; surfaces as OperationCanceledException from JudgeAsync when requested.</summary>
    public const string Cancelled = "cancelled";

    public static readonly string[] All =
    [
        Disabled, NotAuthorized, MissingSecret, Timeout, RateLimited, Overloaded,
        Authentication, Validation, InvalidResponse, Network, Cancelled,
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    private static readonly HashSet<string> Retryables = new(StringComparer.Ordinal)
    {
        Timeout, RateLimited, Overloaded, Network,
    };

    public static bool IsKnown(string category) =>
        !string.IsNullOrWhiteSpace(category) && Known.Contains(category);

    public static bool IsRetryable(string? category) =>
        category is not null && Retryables.Contains(category);
}

internal static class JudgmentProbability
{
    public static void RequireUnitInterval(double value, string what)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            throw new JudgmentValidationException($"{what} must be a finite number in [0,1].");
    }

    public static void RequireFinite(double value, string what)
    {
        if (!double.IsFinite(value))
            throw new JudgmentValidationException($"{what} must be a finite number.");
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulAnswer), "noul")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
public abstract class JudgmentAnswer
{
    public abstract void Validate(string questionId);
}

public sealed class NoulAnswer : JudgmentAnswer
{
    [JsonPropertyName("probabilityYes")] public required double ProbabilityYes { get; init; }

    public override void Validate(string questionId)
    {
        JudgmentProbability.RequireUnitInterval(ProbabilityYes, $"Noul '{questionId}' probabilityYes");
    }
}

public sealed class ChoiceAnswer : JudgmentAnswer
{
    [JsonPropertyName("choice")] public required string Choice { get; init; }
    [JsonPropertyName("probabilities")] public required IReadOnlyDictionary<string, double> Probabilities { get; init; }
    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    public override void Validate(string questionId)
    {
        if (string.IsNullOrWhiteSpace(Choice))
            throw new JudgmentValidationException($"Choice '{questionId}' winner must be non-empty.");
        if (Probabilities is null || Probabilities.Count == 0)
            throw new JudgmentValidationException($"Choice '{questionId}' probabilities are required.");
        JudgmentProbability.RequireUnitInterval(Confidence, $"Choice '{questionId}' confidence");

        foreach (var (option, p) in Probabilities)
        {
            if (string.IsNullOrWhiteSpace(option))
                throw new JudgmentValidationException($"Choice '{questionId}' probability keys must be non-empty.");
            JudgmentProbability.RequireUnitInterval(p, $"Choice '{questionId}' probability for '{option}'");
        }

        if (!Probabilities.ContainsKey(Choice))
            throw new JudgmentValidationException($"Choice '{questionId}' winner '{Choice}' missing from probabilities.");
    }

    /// <summary>Rejects when expected option keys are incomplete relative to the question criteria.</summary>
    public void ValidateAgainstCriteria(string questionId, IReadOnlyDictionary<string, string> criteria)
    {
        Validate(questionId);
        foreach (var option in criteria.Keys)
        {
            if (!Probabilities.ContainsKey(option))
                throw new JudgmentValidationException($"Choice '{questionId}' probabilities missing option '{option}'.");
        }
    }
}

public sealed class ScoreAnswer : JudgmentAnswer
{
    [JsonPropertyName("score")] public required double Score { get; init; }
    [JsonPropertyName("legend")] public required IReadOnlyDictionary<string, string> Legend { get; init; }
    [JsonPropertyName("probabilities")] public required IReadOnlyDictionary<string, double> Probabilities { get; init; }
    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    public override void Validate(string questionId)
    {
        JudgmentProbability.RequireFinite(Score, $"Score '{questionId}' score");
        if (Legend is null || Legend.Count == 0)
            throw new JudgmentValidationException($"Score '{questionId}' legend is required.");
        if (Probabilities is null || Probabilities.Count == 0)
            throw new JudgmentValidationException($"Score '{questionId}' probabilities are required.");
        JudgmentProbability.RequireUnitInterval(Confidence, $"Score '{questionId}' confidence");

        foreach (var (level, p) in Probabilities)
        {
            if (string.IsNullOrWhiteSpace(level))
                throw new JudgmentValidationException($"Score '{questionId}' probability keys must be non-empty.");
            JudgmentProbability.RequireUnitInterval(p, $"Score '{questionId}' probability for '{level}'");
        }
    }

    public void ValidateAgainstLevels(string questionId, IReadOnlyList<string> criteria)
    {
        Validate(questionId);
        for (var i = 0; i < criteria.Count; i++)
        {
            var key = i.ToString();
            if (!Probabilities.ContainsKey(key))
                throw new JudgmentValidationException($"Score '{questionId}' probabilities missing level '{key}'.");
        }
    }
}

public sealed class JudgmentValidationException : Exception
{
    public JudgmentValidationException(string message) : base(message) { }
}

public static class JudgmentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JudgmentValidationException($"Failed to deserialize {typeof(T).Name}.");
}
