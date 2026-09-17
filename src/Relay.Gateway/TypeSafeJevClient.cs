using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relay.Core.Judgments;
using Relay.Core.Model;

namespace Relay.Gateway;

/// <summary>
/// Direct async HTTP client for TypeSafe System One (Jev).
/// POST https://api.typesafe.ai/v1/systemone — Bearer auth; body: model, state, questions.
/// API key via <see cref="ISecretStore"/> (env <c>TYPESAFE_API_KEY</c>); never logged or written to fixtures.
/// </summary>
public sealed class TypeSafeJevClient : IJudgmentClient, IDisposable
{
    public const string DefaultEndpoint = JudgmentDefaults.Endpoint;

    private readonly Uri _endpoint;
    private readonly ISecretStore _secrets;
    private readonly string _secretName;
    private readonly HttpClient _http;
    private readonly bool _disposeHttp;
    private static readonly Regex SecretRedaction = new(@"sk-[A-Za-z0-9_\-]+|Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TypeSafeJevClient(
        ISecretStore secrets,
        string? model = null,
        string secretName = JudgmentDefaults.ApiKeySecretName,
        string? endpoint = null,
        HttpMessageHandler? handler = null,
        bool disposeHandler = true)
    {
        _secrets = secrets;
        _secretName = secretName;
        Model = string.IsNullOrWhiteSpace(model) ? JudgmentDefaults.ModelAlias : model;
        _endpoint = new Uri(endpoint ?? DefaultEndpoint, UriKind.Absolute);
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler)
        {
            Timeout = JudgmentDefaults.AttemptTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Relay/0.2-jev");
        _disposeHttp = true;
    }

    /// <summary>Test constructor using a shared <see cref="HttpClient"/> (handler owned by caller).</summary>
    public TypeSafeJevClient(ISecretStore secrets, HttpClient http, string? model = null, string secretName = JudgmentDefaults.ApiKeySecretName)
    {
        _secrets = secrets;
        _secretName = secretName;
        Model = string.IsNullOrWhiteSpace(model) ? JudgmentDefaults.ModelAlias : model;
        _endpoint = http.BaseAddress ?? new Uri(DefaultEndpoint);
        _http = http;
        _disposeHttp = false;
    }

    public string Name => "typesafe-jev";
    public string Model { get; }

    /// <summary>Live preflight: throws when the API key secret is absent.</summary>
    public void EnsureLivePreflight() => JudgmentLivePreflight.EnsureApiKey(_secrets, _secretName);

    public async Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var key = _secrets.Get(_secretName);
        if (string.IsNullOrWhiteSpace(key))
            return JudgmentResponse.Fail(JudgmentErrorCodes.MissingApiKey,
                $"No API key under secret '{_secretName}'.", retryable: false);

        var wire = BuildWireBody(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new StringContent(wire, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is 401 or 403)
                return JudgmentResponse.Fail(JudgmentErrorCodes.AuthBlocked, Redact($"HTTP {status}: {text}"), status, retryable: false);
            if (status == 422)
                return JudgmentResponse.Fail(JudgmentErrorCodes.InvalidContract, Redact($"HTTP 422: {text}"), status, retryable: false);
            if (status == 429)
                return JudgmentResponse.Fail(JudgmentErrorCodes.Throttled, Redact($"HTTP 429: {text}"), status, retryable: true);
            if (status == 529)
                return JudgmentResponse.Fail(JudgmentErrorCodes.Overload, Redact($"HTTP 529: {text}"), status, retryable: true);
            if (status >= 500)
                return JudgmentResponse.Fail(JudgmentErrorCodes.Transient, Redact($"HTTP {status}: {text}"), status, retryable: true);
            if (!response.IsSuccessStatusCode)
                return JudgmentResponse.Fail(JudgmentErrorCodes.Transient, Redact($"HTTP {status}: {text}"), status, retryable: status >= 500);

            var validated = JudgmentResponseValidator.Validate(request, text, httpStatus: status);
            if (!validated.Ok)
                return validated;

            // Preserve model identity from the provider when present.
            return validated;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return JudgmentResponse.Fail(JudgmentErrorCodes.Transient,
                $"Timed out after {JudgmentDefaults.AttemptTimeout.TotalSeconds:0}s ({watch.ElapsedMilliseconds}ms).",
                retryable: true);
        }
        catch (OperationCanceledException)
        {
            return JudgmentResponse.Fail(JudgmentErrorCodes.Cancelled, "Cancelled.", retryable: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return JudgmentResponse.Fail(JudgmentErrorCodes.Transient, Redact(ex.GetType().Name + ": " + ex.Message), retryable: true);
        }
    }

    public static string BuildWireBody(JudgmentRequest request)
    {
        // Wire shape: model, state, questions — never include secrets.
        var questions = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, q) in request.Questions)
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = q.Type,
                ["instructions"] = q.Instructions,
            };
            if (q.Criteria is { } c && c.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                obj["criteria"] = JsonSerializer.Deserialize<object>(c.GetRawText(), JudgmentJson.Options);
            questions[id] = obj;
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Model) ? JudgmentDefaults.ModelAlias : request.Model,
            ["state"] = JsonSerializer.Deserialize<object>(request.State.GetRawText(), JudgmentJson.Options),
            ["questions"] = questions,
        };
        return JsonSerializer.Serialize(body, JudgmentJson.Options);
    }

    /// <summary>Redacts bearer tokens / sk- keys from error surfaces (never log raw secrets).</summary>
    public static string Redact(string text)
        => SecretRedaction.Replace(text, "[REDACTED]");

    public void Dispose()
    {
        if (_disposeHttp) _http.Dispose();
    }
}
