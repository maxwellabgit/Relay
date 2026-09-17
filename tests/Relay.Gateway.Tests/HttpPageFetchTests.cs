using Relay.Core.Cases;
using Relay.Gateway;

namespace Relay.Gateway.Tests;

public class HttpPageFetchTests
{
    [Fact]
    public async Task Rejects_redirect_to_non_allowlisted_host()
    {
        var handler = new RedirectHandler(new Uri("https://evil.example/leak"));
        using var fetch = new HttpPageFetch(["safe.example"], handler);
        var result = await fetch.FetchAsync("https://safe.example/page");
        Assert.False(result.Ok);
        Assert.Equal("redirect_host_not_allowed", result.Error);
        Assert.Equal("https://evil.example/leak", result.FinalUrl);
    }

    [Fact]
    public async Task Rejects_non_https()
    {
        using var fetch = new HttpPageFetch(["safe.example"]);
        var result = await fetch.FetchAsync("http://safe.example/page");
        Assert.False(result.Ok);
        Assert.Equal("https_required", result.Error);
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        private readonly Uri _location;
        public RedirectHandler(Uri location) => _location = location;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Redirect)
            {
                Headers = { Location = _location },
                RequestMessage = request,
            });
    }
}
