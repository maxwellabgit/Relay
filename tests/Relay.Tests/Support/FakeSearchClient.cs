using Relay.Core.Search;

namespace Relay.Tests.Support;

/// <summary>In-memory search provider for tests — no network.</summary>
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

    public FakeSearchClient Fail(string error, int? status = null)
    {
        _replies.Enqueue(SearchResponse.Failed(error, 5, status));
        return this;
    }

    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_replies.Count > 0) return Task.FromResult(_replies.Dequeue());
        return Task.FromResult(new SearchResponse(true,
        [
            new SearchHitResult("Result for " + request.Query, "https://example.test/" + Uri.EscapeDataString(request.Query), "A stub snippet about " + request.Query + ".")
        ], 8, null, 200));
    }
}
