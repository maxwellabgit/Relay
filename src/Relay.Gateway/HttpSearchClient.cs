using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relay.Core.Config;
using Relay.Core.Model;
using Relay.Core.Search;

namespace Relay.Gateway;

/// <summary>
/// HTTPS search client for one allow-listed endpoint (Brave Search API shape, or any host that returns
/// the same JSON). No redirects, no proxy, key from the secret store per call, errors as data.
/// When the endpoint or secret is unset the call fails clearly — nothing is invented.
/// </summary>
public sealed class HttpSearchClient : ISearchClient, IDisposable
{
    private readonly Uri _endpoint;
    private readonly ISecretStore _secrets;
    private readonly string _secretName;
    private readonly HttpClient _http;

    public HttpSearchClient(SearchSettings settings, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        if (!SearchSettings.IsAllowedEndpoint(settings.Endpoint, out var why) || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("The search endpoint " + why, nameof(settings));
        _endpoint = endpoint;
        _secrets = secrets;
        _secretName = settings.SecretName;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Relay/0.2");
    }

    public string Host => _endpoint.Host;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(request.Query)) return SearchResponse.Failed("Search needs a query.", watch.ElapsedMilliseconds);
        var key = _secrets.Get(_secretName);
        if (string.IsNullOrWhiteSpace(key)) return SearchResponse.Failed($"No API key stored under secret '{_secretName}'.", watch.ElapsedMilliseconds);

        var limit = Math.Clamp(request.Limit, 1, 20);
        var url = BuildUrl(request.Query.Trim(), limit);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.TryAddWithoutValidation("X-Subscription-Token", key);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SearchResponse.Failed($"HTTP {(int)response.StatusCode} from {Host}: {ErrorMessage(text)}", watch.ElapsedMilliseconds, (int)response.StatusCode);
            return Parse(text, watch.ElapsedMilliseconds, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SearchResponse.Failed($"Timed out after {_http.Timeout.TotalSeconds:0}s.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or IOException or UriFormatException)
        {
            return SearchResponse.Failed(ex.GetType().Name + ": " + ex.Message, watch.ElapsedMilliseconds);
        }
    }

    private Uri BuildUrl(string query, int limit)
    {
        var sep = _endpoint.Query.Length > 0 ? "&" : "?";
        var q = Uri.EscapeDataString(query);
        var count = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Uri(_endpoint.AbsoluteUri + sep + "q=" + q + "&count=" + count);
    }

    private static SearchResponse Parse(string text, long elapsedMs, int status)
    {
        var root = JsonNode.Parse(text)?.AsObject();
        if (root is null) return SearchResponse.Failed("Response was not JSON.", elapsedMs, status);

        // Brave: web.results[]; stub / other: hits[]
        var array = root["web"]?["results"]?.AsArray() ?? root["hits"]?.AsArray();
        if (array is null) return SearchResponse.Failed("Response had no web.results or hits array.", elapsedMs, status);

        var hits = new List<SearchHitResult>();
        foreach (var item in array)
        {
            if (item is not JsonObject o) continue;
            var title = o["title"]?.GetValue<string>() ?? "";
            var url = o["url"]?.GetValue<string>() ?? "";
            var snippet = o["description"]?.GetValue<string>() ?? o["snippet"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(snippet)) continue;
            hits.Add(new SearchHitResult(title.Trim(), url.Trim(), snippet.Trim()));
        }
        return new SearchResponse(true, hits, elapsedMs, null, status);
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            var msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>()
                ?? JsonNode.Parse(body)?["message"]?.GetValue<string>();
            if (msg is not null) return msg.Length > 300 ? msg[..299] + "…" : msg;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..299] + "…" : body;
    }

    public void Dispose() => _http.Dispose();
}
