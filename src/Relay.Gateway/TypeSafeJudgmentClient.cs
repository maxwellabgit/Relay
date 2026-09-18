using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Config;
using Relay.Core.Judgments;
using Relay.Core.Model;

namespace Relay.Gateway;

/// <summary>
/// Direct HTTPS client for TypeSafe System One. HTTPS only, no redirects, secret read per request.
/// Retries only 429/529 and pre-response connection failures.
/// </summary>
public sealed class TypeSafeJudgmentClient : IJudgmentClient, IDisposable
{
    public const string DefaultEndpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultModel = "jev-1.13.0";
    public const decimal DollarsPerMillionInputTokens = 0.042m;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
    ];

    private readonly Uri _endpoint;
    private readonly ISecretStore _secrets;
    private readonly string _secretName;
    private readonly bool _enabled;
    private readonly int _maxAttempts;
    private readonly HttpClient _http;
    private readonly string _configuredModel;

    public TypeSafeJudgmentClient(JevSettings settings, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);

        if (!JevSettings.IsAllowedEndpoint(settings.Endpoint, out var why) ||
            !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The Jev endpoint " + why, nameof(settings));
        }

        _endpoint = endpoint;
        _secrets = secrets;
        _secretName = settings.SecretName;
        _enabled = settings.Enabled;
        _maxAttempts = Math.Clamp(settings.MaxAttempts, 1, 5);
        _configuredModel = string.IsNullOrWhiteSpace(settings.Model) ? DefaultModel : settings.Model.Trim();
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, settings.TimeoutMs)),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Relay/0.2");
    }

    public string ProviderName => "typesafe";
    public Uri Endpoint => _endpoint;
    public string ConfiguredModel => _configuredModel;

    public async Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Validate();
        }
        catch (JudgmentValidationException ex)
        {
            return JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.Validation, ex.Message));
        }

        if (!_enabled)
        {
            return JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.Disabled, "Hosted judgments are disabled."));
        }

        var key = _secrets.Get(_secretName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.MissingSecret, $"No API key stored under secret '{_secretName}'."));
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? _configuredModel : request.Model.Trim();
        var body = TypeSafeRequestBuilder.Build(request.State, model, request.Questions);
        var payload = JsonSerializer.Serialize(body, TypeSafeJson.Options);

        Exception? lastConnectionError = null;
        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var message = CreateMessage(payload, key);
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (status is 429 or 529)
                {
                    if (attempt >= _maxAttempts - 1)
                    {
                        var category = status == 429
                            ? JudgmentFailureCategories.RateLimited
                            : JudgmentFailureCategories.Overloaded;
                        return JudgmentResponse.FromFailure(
                            JudgmentFailure.Create(category, status == 429 ? "TypeSafe rate limited the request." : "TypeSafe is temporarily overloaded.", httpStatus: status));
                    }

                    await DelayBeforeRetryAsync(response, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (status == 401)
                {
                    return JudgmentResponse.FromFailure(
                        JudgmentFailure.Create(JudgmentFailureCategories.Authentication, "TypeSafe rejected the API key.", httpStatus: 401));
                }

                if (status == 422)
                {
                    return JudgmentResponse.FromFailure(
                        JudgmentFailure.Create(JudgmentFailureCategories.Validation, "TypeSafe rejected the request as invalid.", httpStatus: 422));
                }

                if (status is < 200 or >= 300)
                {
                    return JudgmentResponse.FromFailure(
                        JudgmentFailure.Create(JudgmentFailureCategories.Network, $"TypeSafe returned HTTP {status}.", httpStatus: status));
                }

                if (!TypeSafeResponseParser.TryParse(text, watch.ElapsedMilliseconds, out var success, out var parseError))
                {
                    return JudgmentResponse.FromFailure(
                        JudgmentFailure.Create(JudgmentFailureCategories.InvalidResponse, parseError ?? "TypeSafe response failed validation."));
                }

                try
                {
                    success!.ValidateAgainst(request);
                }
                catch (JudgmentValidationException ex)
                {
                    return JudgmentResponse.FromFailure(
                        JudgmentFailure.Create(JudgmentFailureCategories.InvalidResponse, ex.Message));
                }

                return JudgmentResponse.FromSuccess(success);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return JudgmentResponse.FromFailure(
                    JudgmentFailure.Create(JudgmentFailureCategories.Timeout, "TypeSafe request timed out."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastConnectionError = ex;
                if (attempt >= _maxAttempts - 1)
                    break;
                await Task.Delay(RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)], cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                lastConnectionError = ex;
                if (attempt >= _maxAttempts - 1)
                    break;
                await Task.Delay(RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)], cancellationToken).ConfigureAwait(false);
            }
        }

        _ = lastConnectionError;
        return JudgmentResponse.FromFailure(
            JudgmentFailure.Create(JudgmentFailureCategories.Network, "TypeSafe connection failed before a response."));
    }

    public static decimal EstimateCostUsd(int inputTokens) =>
        inputTokens <= 0 ? 0m : inputTokens * DollarsPerMillionInputTokens / 1_000_000m;

    private HttpRequestMessage CreateMessage(string payload, string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        return message;
    }

    private static async Task DelayBeforeRetryAsync(HttpResponseMessage response, int attempt, CancellationToken cancellationToken)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(delta, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromMinutes(2))
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await Task.Delay(RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)], cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}

internal static class TypeSafeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

internal static class TypeSafeRequestBuilder
{
    public static Dictionary<string, object?> Build(
        JsonElement state,
        string model,
        IReadOnlyDictionary<string, JudgmentQuestion> questions)
    {
        var wireQuestions = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (id, question) in questions)
        {
            wireQuestions[id] = question switch
            {
                NoulQuestion noul => new Dictionary<string, object?>
                {
                    ["type"] = "noul",
                    ["instructions"] = noul.Instructions,
                    ["criteria"] = noul.Criteria,
                },
                ChoiceQuestion choice => new Dictionary<string, object?>
                {
                    ["type"] = "choice",
                    ["instructions"] = choice.Instructions,
                    ["criteria"] = choice.Criteria,
                },
                ScoreQuestion score => new Dictionary<string, object?>
                {
                    ["type"] = "score",
                    ["instructions"] = score.Instructions,
                    ["criteria"] = score.Criteria,
                },
                _ => throw new JudgmentValidationException($"Unsupported question type for '{id}'."),
            };
        }

        return new Dictionary<string, object?>
        {
            ["state"] = state,
            ["model"] = model,
            ["questions"] = wireQuestions,
        };
    }
}
