using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Config;
using Relay.Core.Model;

namespace Relay.Gateway;

/// <summary>
/// Chat-completions client for one allow-listed endpoint. It sends the request Relay built and
/// nothing more: no telemetry, no retries that could duplicate side effects, no redirects, and the
/// key is read from the secret store per call so it is never held in settings or long-lived state.
/// Errors are returned as data; nothing here throws for network conditions.
/// </summary>
public sealed class OpenAiCompatibleClient : IModelClient, IDisposable
{
    private readonly Uri _endpoint;
    private readonly ISecretStore _secrets;
    private readonly string _secretName;
    private readonly HttpClient _http;

    public OpenAiCompatibleClient(ModelSettings settings, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The model endpoint must be an absolute https URL.", nameof(settings));
        _endpoint = endpoint;
        _secrets = secrets;
        _secretName = settings.SecretName;
        Model = settings.Model;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Relay/0.1");
    }

    public string Host => _endpoint.Host;
    public string Model { get; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var key = _secrets.Get(_secretName);
        if (string.IsNullOrWhiteSpace(key)) return ModelResponse.Failed($"No API key stored under secret '{_secretName}'.", watch.ElapsedMilliseconds);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = 0,
        };
        if (request.JsonObject) body["response_format"] = new JsonObject { ["type"] = "json_object" };

        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ModelResponse.Failed($"HTTP {(int)response.StatusCode} from {Host}: {ErrorMessage(text)}", watch.ElapsedMilliseconds, (int)response.StatusCode);

            var root = JsonNode.Parse(text)?.AsObject();
            var choices = root?["choices"]?.AsArray();
            var content = choices is { Count: > 0 } ? choices[0]?["message"]?["content"]?.GetValue<string>() : null;
            if (content is null) return ModelResponse.Failed("Response had no choices[0].message.content.", watch.ElapsedMilliseconds, (int)response.StatusCode);
            var usage = root?["usage"];
            return new ModelResponse(true, content, usage?["prompt_tokens"]?.GetValue<int>() ?? 0, usage?["completion_tokens"]?.GetValue<int>() ?? 0, watch.ElapsedMilliseconds, null, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelResponse.Failed($"Timed out after {_http.Timeout.TotalSeconds:0}s.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return ModelResponse.Failed(ex.GetType().Name + ": " + ex.Message, watch.ElapsedMilliseconds);
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            var msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
            if (msg is not null) return msg.Length > 300 ? msg[..299] + "…" : msg;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..299] + "…" : body;
    }

    public void Dispose() => _http.Dispose();
}
