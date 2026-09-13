namespace Relay.Core.Search;

/// <summary>One web search request. The gateway sends exactly this and nothing else.</summary>
public sealed record SearchRequest(string Query, int Limit = 8);

/// <summary>One result from a search provider, before Relay stores it as a local artifact.</summary>
public sealed record SearchHitResult(string Title, string Url, string Snippet);

/// <summary>
/// One search response. Errors are data: a failed call never throws for network conditions.
/// Implementations live outside Relay.Core (the core has no network).
/// </summary>
public sealed record SearchResponse(bool Ok, IReadOnlyList<SearchHitResult> Hits, long ElapsedMs, string? Error, int? HttpStatus = null)
{
    public static SearchResponse Failed(string error, long elapsedMs, int? status = null)
        => new(false, [], elapsedMs, error, status);
}

/// <summary>
/// The only way an online search provider is reached. Called only through Relay.Gateway under a
/// standing grant or a per-task approval; results are stored as citable local artifacts.
/// </summary>
public interface ISearchClient
{
    string Host { get; }
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}
