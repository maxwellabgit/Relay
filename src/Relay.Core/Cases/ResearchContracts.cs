using Relay.Core.Search;

namespace Relay.Core.Cases;

/// <summary>Allow-listed page fetch. Implementations live outside the core for real HTTP.</summary>
public interface IPageFetch
{
    /// <summary>Hosts this adapter will fetch (lowercase hostnames).</summary>
    IReadOnlyList<string> AllowedHosts { get; }

    Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);
}

public sealed record PageFetchResult(bool Ok, string Url, string? Body, string? Error, long ElapsedMs, string? FinalUrl = null);

/// <summary>External delegate transport for research packages. No canonical writes.</summary>
public interface IDelegateClient
{
    string Profile { get; }

    Task<DelegateResponse> CompleteAsync(DelegateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Delegate package with permitted excerpt bodies (hashes + selectors).
/// Does not imply every input is a citation — callers must pass explicit citation ids separately.
/// </summary>
public sealed record DelegateRequest(
    string PackageId,
    string Profile,
    string Objective,
    IReadOnlyList<string> ArtifactObjectIds,
    IReadOnlyList<DelegateSource> Sources,
    IReadOnlyList<DelegateExcerpt> PermittedExcerpts,
    IReadOnlyList<string> ExplicitCitationIds);

public sealed record DelegateSource(string ObjectId, string Sha256, string Kind, string Title, string? Url);

/// <summary>Permitted source excerpt body with content hash and selector offsets.</summary>
public sealed record DelegateExcerpt(
    string ArtifactId,
    string ContentHash,
    string Text,
    int StartOffset,
    int EndOffset,
    string? Selector = null);

public sealed record DelegateResponse(bool Ok, string Text, string? Error, long ElapsedMs);

/// <summary>Injectable research adapters bound into <see cref="CaseRuntime"/>.</summary>
public sealed class ResearchServices
{
    public ISearchClient? Search { get; init; }
    public IPageFetch? Fetch { get; init; }
    public IDelegateClient? Delegate { get; init; }

    public bool SearchAvailable => Search is not null;
    public bool DelegateAvailable => Delegate is not null;
}

public static class ResearchCapabilities
{
    public const string Search = "research.search";
    public const string Delegate = "research.delegate";
    public const string ModelRequest = "model.request";
}
