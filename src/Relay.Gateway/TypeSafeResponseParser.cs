using System.Text.Json;
using Relay.Core.Judgments;

namespace Relay.Gateway;

/// <summary>Parses TypeSafe System One wire JSON into Relay <see cref="JudgmentSuccess"/>.</summary>
public static class TypeSafeResponseParser
{
    public static bool TryParse(string json, long elapsedMs, out JudgmentSuccess? success, out string? error)
    {
        success = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty TypeSafe response body.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "TypeSafe response root must be an object.";
                return false;
            }

            if (!root.TryGetProperty("model", out var modelEl) || modelEl.ValueKind != JsonValueKind.String)
            {
                error = "TypeSafe response missing model.";
                return false;
            }

            var model = modelEl.GetString();
            if (string.IsNullOrWhiteSpace(model))
            {
                error = "TypeSafe response model was empty.";
                return false;
            }

            if (!root.TryGetProperty("answers", out var answersEl) || answersEl.ValueKind != JsonValueKind.Object)
            {
                error = "TypeSafe response missing answers object.";
                return false;
            }

            var answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal);
            foreach (var property in answersEl.EnumerateObject())
            {
                if (!TryParseAnswer(property.Name, property.Value, out var answer, out error))
                    return false;
                answers[property.Name] = answer!;
            }

            if (answers.Count == 0)
            {
                error = "TypeSafe response contained no answers.";
                return false;
            }

            var inputTokens = 0;
            var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inTok))
                    inputTokens = inTok;
                if (usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var outTok))
                    outputTokens = outTok;
            }

            string? providerRequestId = null;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                providerRequestId = idEl.GetString();
            else if (root.TryGetProperty("request_id", out var reqEl) && reqEl.ValueKind == JsonValueKind.String)
                providerRequestId = reqEl.GetString();

            success = new JudgmentSuccess
            {
                Model = model!,
                Answers = answers,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ElapsedMs = Math.Max(0, elapsedMs),
                ProviderRequestId = providerRequestId,
            };
            success.Validate();
            return true;
        }
        catch (JsonException)
        {
            error = "TypeSafe response was not valid JSON.";
            return false;
        }
        catch (JudgmentValidationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseAnswer(string id, JsonElement el, out JudgmentAnswer? answer, out string? error)
    {
        answer = null;
        error = null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            error = $"Answer '{id}' must be an object.";
            return false;
        }

        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            error = $"Answer '{id}' missing type.";
            return false;
        }

        var type = typeEl.GetString();
        switch (type)
        {
            case "noul":
                if (!el.TryGetProperty("noul", out var noulEl) || !noulEl.TryGetDouble(out var probability))
                {
                    error = $"Noul answer '{id}' missing noul probability.";
                    return false;
                }
                answer = new NoulAnswer { ProbabilityYes = probability };
                break;

            case "choice":
                if (!el.TryGetProperty("choice", out var choiceEl) || choiceEl.ValueKind != JsonValueKind.String)
                {
                    error = $"Choice answer '{id}' missing choice.";
                    return false;
                }
                if (!el.TryGetProperty("probabilities", out var choiceProbs) || choiceProbs.ValueKind != JsonValueKind.Object)
                {
                    error = $"Choice answer '{id}' missing probabilities.";
                    return false;
                }
                if (!el.TryGetProperty("confidence", out var choiceConf) || !choiceConf.TryGetDouble(out var choiceConfidence))
                {
                    error = $"Choice answer '{id}' missing confidence.";
                    return false;
                }
                answer = new ChoiceAnswer
                {
                    Choice = choiceEl.GetString()!,
                    Confidence = choiceConfidence,
                    Probabilities = ReadProbabilityMap(choiceProbs),
                };
                break;

            case "score":
                if (!el.TryGetProperty("score", out var scoreEl) || !scoreEl.TryGetDouble(out var score))
                {
                    error = $"Score answer '{id}' missing score.";
                    return false;
                }
                if (!el.TryGetProperty("legend", out var legendEl) || legendEl.ValueKind != JsonValueKind.Object)
                {
                    error = $"Score answer '{id}' missing legend.";
                    return false;
                }
                if (!el.TryGetProperty("probabilities", out var scoreProbs) || scoreProbs.ValueKind != JsonValueKind.Object)
                {
                    error = $"Score answer '{id}' missing probabilities.";
                    return false;
                }
                if (!el.TryGetProperty("confidence", out var scoreConf) || !scoreConf.TryGetDouble(out var scoreConfidence))
                {
                    error = $"Score answer '{id}' missing confidence.";
                    return false;
                }
                var legend = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in legendEl.EnumerateObject())
                    legend[prop.Name] = prop.Value.GetString() ?? "";
                answer = new ScoreAnswer
                {
                    Score = score,
                    Confidence = scoreConfidence,
                    Legend = legend,
                    Probabilities = ReadProbabilityMap(scoreProbs),
                };
                break;

            default:
                error = $"Unknown answer type '{type}'.";
                return false;
        }

        try
        {
            answer.Validate(id);
            return true;
        }
        catch (JudgmentValidationException ex)
        {
            error = ex.Message;
            answer = null;
            return false;
        }
    }

    private static Dictionary<string, double> ReadProbabilityMap(JsonElement obj)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!prop.Value.TryGetDouble(out var p))
                throw new JudgmentValidationException($"Malformed probability for '{prop.Name}'.");
            map[prop.Name] = p;
        }
        return map;
    }
}
