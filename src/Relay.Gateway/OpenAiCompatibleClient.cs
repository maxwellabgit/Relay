using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Config;
using Relay.Core.Model;

namespace Relay.Gateway;

/// <summary>
/// Chat-completions client for one allow-listed endpoint: https anywhere, or plain http strictly on
/// loopback (the local llama.cpp server that hosts RELAY0). It sends the request Relay built and
/// nothing more: no telemetry, no retries that could duplicate side effects, no redirects, and the
/// key is read from the secret store per call so it is never held in settings or long-lived state.
/// A loopback server needs no key. Errors are returned as data; nothing here throws for network conditions.
/// <see cref="StreamAsync"/> is the same request with <c>stream: true</c>, read as server-sent events
/// so a delegate's reply can be observed while it is still being written.
/// </summary>
public sealed class OpenAiCompatibleClient : IModelClient, IDisposable
{
    private readonly Uri _endpoint;
    private readonly ISecretStore _secrets;
    private readonly string _secretName;
    private readonly HttpClient _http;

    public OpenAiCompatibleClient(ModelSettings settings, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        if (!ModelSettings.IsAllowedEndpoint(settings.Endpoint, out var why) || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("The model endpoint " + why, nameof(settings));
        _endpoint = endpoint;
        _secrets = secrets;
        _secretName = settings.SecretName;
        Model = settings.Model;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Relay/0.2");
    }

    public string Host => _endpoint.Host;
    public string Model { get; }
    public bool IsLoopback => _endpoint.IsLoopback;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var key = _secrets.Get(_secretName);
        if (string.IsNullOrWhiteSpace(key) && !IsLoopback) return ModelResponse.Failed($"No API key stored under secret '{_secretName}'.", watch.ElapsedMilliseconds);

        using var message = Message(Body(request), key);
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ModelResponse.Failed($"HTTP {(int)response.StatusCode} from {Host}: {ErrorMessage(text)}", watch.ElapsedMilliseconds, (int)response.StatusCode);
            return ParseCompletion(text, watch.ElapsedMilliseconds, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelResponse.Failed($"Timed out after {_http.Timeout.TotalSeconds:0}s.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or IOException)
        {
            return ModelResponse.Failed(ex.GetType().Name + ": " + ex.Message, watch.ElapsedMilliseconds);
        }
    }

    public async Task<ModelResponse> StreamAsync(ModelRequest request, Func<string, CancellationToken, Task> onDelta, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var key = _secrets.Get(_secretName);
        if (string.IsNullOrWhiteSpace(key) && !IsLoopback) return ModelResponse.Failed($"No API key stored under secret '{_secretName}'.", watch.ElapsedMilliseconds);

        var body = Body(request);
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var message = Message(body, key);

        // The client's timeout bounds the whole stream, not just the headers: a host that stops writing is a timeout, not a hang.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_http.Timeout);
        var token = timeout.Token;
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return ModelResponse.Failed($"HTTP {(int)response.StatusCode} from {Host}: {ErrorMessage(text)}", watch.ElapsedMilliseconds, (int)response.StatusCode);
            }
            var status = (int)response.StatusCode;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                // The host ignored stream=true and answered whole; deliver it as one delta so callers see the same thing either way.
                var whole = ParseCompletion(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), watch.ElapsedMilliseconds, status);
                if (whole.Ok && !string.IsNullOrEmpty(whole.Content)) await onDelta(whole.Content, cancellationToken).ConfigureAwait(false);
                return whole;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = new StringBuilder();
            int promptTokens = 0, completionTokens = 0;
            var sawDone = false;
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line[0] == ':') continue;            // blank separators and keep-alive comments
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") { sawDone = true; break; }
                JsonObject? chunk;
                try { chunk = JsonNode.Parse(data)?.AsObject(); } catch (JsonException) { continue; }
                if (chunk is null) continue;
                if (chunk["error"] is JsonObject error)
                    return ModelResponse.Failed($"Stream error from {Host}: {error["message"]?.GetValue<string>() ?? data}", watch.ElapsedMilliseconds, status);
                if (chunk["usage"] is JsonObject usage)
                {
                    promptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? promptTokens;
                    completionTokens = usage["completion_tokens"]?.GetValue<int>() ?? completionTokens;
                }
                var choices = chunk["choices"]?.AsArray();
                if (choices is not { Count: > 0 }) continue;
                var delta = choices[0]?["delta"]?["content"];
                var piece = delta is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (string.IsNullOrEmpty(piece)) continue;
                content.Append(piece);
                await onDelta(piece, cancellationToken).ConfigureAwait(false);
            }
            if (content.Length == 0 && !sawDone) return ModelResponse.Failed("The stream ended without content.", watch.ElapsedMilliseconds, status);
            return new ModelResponse(true, content.ToString(), promptTokens, completionTokens, watch.ElapsedMilliseconds, null, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelResponse.Failed($"Timed out after {_http.Timeout.TotalSeconds:0}s.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or IOException)
        {
            return ModelResponse.Failed(ex.GetType().Name + ": " + ex.Message, watch.ElapsedMilliseconds);
        }
    }

    private static JsonObject Body(ModelRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = 0,
        };
        if (request.JsonSchema is not null)
        {
            JsonNode? schema = null;
            try { schema = JsonNode.Parse(request.JsonSchema); } catch (JsonException) { }
            if (schema is not null)
                body["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = request.SchemaName, ["strict"] = true, ["schema"] = schema } };
            else if (request.JsonObject) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        else if (request.JsonObject) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body;
    }

    private HttpRequestMessage Message(JsonObject body, string? key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    private static ModelResponse ParseCompletion(string text, long elapsedMs, int status)
    {
        var root = JsonNode.Parse(text)?.AsObject();
        var choices = root?["choices"]?.AsArray();
        var content = choices is { Count: > 0 } ? choices[0]?["message"]?["content"]?.GetValue<string>() : null;
        if (content is null) return ModelResponse.Failed("Response had no choices[0].message.content.", elapsedMs, status);
        var usage = root?["usage"];
        return new ModelResponse(true, content, usage?["prompt_tokens"]?.GetValue<int>() ?? 0, usage?["completion_tokens"]?.GetValue<int>() ?? 0, elapsedMs, null, status);
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
