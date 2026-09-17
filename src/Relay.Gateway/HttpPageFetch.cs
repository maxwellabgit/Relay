using System.Diagnostics;
using Relay.Core.Cases;

namespace Relay.Gateway;

/// <summary>
/// Allow-listed HTTPS page fetch. Redirects are validated against the allow-list;
/// off-list or open redirect targets are refused.
/// </summary>
public sealed class HttpPageFetch : IPageFetch, IDisposable
{
    private readonly HashSet<string> _allowed;
    private readonly HttpClient _http;

    public HttpPageFetch(IEnumerable<string> allowedHosts, HttpMessageHandler? handler = null, int timeoutMs = 15_000)
    {
        _allowed = new HashSet<string>(allowedHosts.Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Relay/0.2");
    }

    public IReadOnlyList<string> AllowedHosts => _allowed.ToList();

    public async Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new PageFetchResult(false, url, null, "https_required", watch.ElapsedMilliseconds);

        if (!_allowed.Contains(uri.Host.ToLowerInvariant()))
            return new PageFetchResult(false, url, null, "host_not_allowed", watch.ElapsedMilliseconds);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            // Validate redirects explicitly — do not follow off-list targets.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                    return new PageFetchResult(false, url, null, "redirect_missing_location", watch.ElapsedMilliseconds, response.RequestMessage?.RequestUri?.ToString());
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Scheme != Uri.UriSchemeHttps)
                    return new PageFetchResult(false, url, null, "redirect_not_https", watch.ElapsedMilliseconds, next.ToString());
                if (!_allowed.Contains(next.Host.ToLowerInvariant()))
                    return new PageFetchResult(false, url, null, "redirect_host_not_allowed", watch.ElapsedMilliseconds, next.ToString());
                return new PageFetchResult(false, url, null, "redirect_requires_revalidation", watch.ElapsedMilliseconds, next.ToString());
            }

            if (!response.IsSuccessStatusCode)
                return new PageFetchResult(false, url, null, $"HTTP {(int)response.StatusCode}", watch.ElapsedMilliseconds);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new PageFetchResult(true, url, body, null, watch.ElapsedMilliseconds, response.RequestMessage?.RequestUri?.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PageFetchResult(false, url, null, "timeout", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new PageFetchResult(false, url, null, ex.GetType().Name + ": " + ex.Message, watch.ElapsedMilliseconds);
        }
    }

    public void Dispose() => _http.Dispose();
}
