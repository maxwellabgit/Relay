using Relay.Core.Cases;
using Relay.Core.Search;

namespace Relay.Core.Tests.Support;

/// <summary>In-memory search provider for Core.Tests — no network.</summary>
public sealed class FakeSearchClient : ISearchClient
{
    private readonly Queue<SearchResponse> _replies = new();
    public List<SearchRequest> Requests { get; } = [];
    public string Host { get; init; } = "search.test";

    public FakeSearchClient Reply(params SearchHitResult[] hits)
    {
        _replies.Enqueue(new SearchResponse(true, hits, 12, null, 200));
        return this;
    }

    public FakeSearchClient Empty()
    {
        _replies.Enqueue(new SearchResponse(true, [], 5, null, 200));
        return this;
    }

    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_replies.Count > 0) return Task.FromResult(_replies.Dequeue());
        return Task.FromResult(new SearchResponse(true,
        [
            new SearchHitResult("Result for " + request.Query, "https://example.test/q", "Stub snippet.")
        ], 8, null, 200));
    }
}

public sealed class FakePageFetch : IPageFetch
{
    private readonly Dictionary<string, string> _pages = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> AllowedHosts { get; init; } = ["lightshift.example", "news.example"];
    public List<string> FetchedUrls { get; } = [];

    public FakePageFetch Page(string url, string body)
    {
        _pages[url] = body;
        return this;
    }

    public Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        FetchedUrls.Add(url);
        if (_pages.TryGetValue(url, out var body))
            return Task.FromResult(new PageFetchResult(true, url, body, null, 3));
        return Task.FromResult(new PageFetchResult(false, url, null, "not in fixture", 1));
    }
}

public sealed class FakeDelegateClient : IDelegateClient
{
    public string Profile { get; init; } = "research-delegate";
    public List<DelegateRequest> Requests { get; } = [];
    public Func<DelegateRequest, DelegateResponse>? Handler { get; set; }

    public Task<DelegateResponse> CompleteAsync(DelegateRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Handler is not null) return Task.FromResult(Handler(request));
        var cites = string.Join(", ", request.ArtifactObjectIds);
        var text = "Based on stored sources (" + cites + "), Lightshift operates 20 battery energy storage sites.";
        return Task.FromResult(new DelegateResponse(true, text, null, 15));
    }
}
