using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;

namespace Relay.Core.Search;

/// <summary>One stored web-search hit: a citable local artifact the mind opens with <c>read_artifact</c>.</summary>
public sealed record SearchArtifact(
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("snippet")] string Snippet,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>
/// Writes and reads search-hit artifacts under <see cref="DataRoot.SearchArtifactsDirectory"/>.
/// The text a tool returns for citation is title, URL and snippet — never a live fetch of the page.
/// </summary>
public sealed class SearchArtifacts
{
    private readonly DataRoot _root;
    private readonly Func<DateTimeOffset> _clock;

    public SearchArtifacts(DataRoot root, Func<DateTimeOffset>? clock = null)
    {
        _root = root;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_root.SearchArtifactsDirectory);
    }

    public string? Read(string artifactId)
    {
        var record = ReadRecord(artifactId);
        return record is null ? null : Format(record);
    }

    public SearchArtifact? ReadRecord(string artifactId)
    {
        if (!Ulid.IsValid(artifactId)) return null;
        var path = Path.Combine(_root.SearchArtifactsDirectory, artifactId + ".artifact.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SearchArtifact>(File.ReadAllText(path), RelayJson.Indented); }
        catch (JsonException) { return null; }
    }

    public SearchArtifact Store(string query, SearchHitResult hit, string host)
    {
        var at = _clock();
        var text = Format(hit.Title, hit.Url, hit.Snippet);
        var artifact = new SearchArtifact(Ulid.NewUlid(at), query, hit.Title, hit.Url, hit.Snippet, host, Sha(text), at);
        AtomicFile.WriteAllText(Path.Combine(_root.SearchArtifactsDirectory, artifact.ArtifactId + ".artifact.json"),
            JsonSerializer.Serialize(artifact, RelayJson.Indented));
        return artifact;
    }

    public IEnumerable<(string Id, string Text, DateTimeOffset At)> All()
    {
        if (!Directory.Exists(_root.SearchArtifactsDirectory)) yield break;
        foreach (var file in Directory.EnumerateFiles(_root.SearchArtifactsDirectory, "*.artifact.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            SearchArtifact? artifact = null;
            try { artifact = JsonSerializer.Deserialize<SearchArtifact>(File.ReadAllText(file), RelayJson.Indented); } catch (JsonException) { }
            if (artifact is not null) yield return (artifact.ArtifactId, Format(artifact), artifact.At);
        }
    }

    public static string Format(SearchArtifact a) => Format(a.Title, a.Url, a.Snippet);

    private static string Format(string title, string url, string snippet)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.Append(title.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(url)) sb.Append(url.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(snippet)) sb.Append(snippet.Trim());
        return sb.ToString().Trim();
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
