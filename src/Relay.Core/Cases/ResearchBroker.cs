using System.Diagnostics;
using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Policy;
using Relay.Core.Search;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>
/// Orchestrates the research path: approved search scope → hits → allow-listed fetch →
/// object-store artifacts → delegate package with exact refs → stored response.
/// External workers never receive canonical write access.
/// </summary>
public sealed class ResearchBroker
{
    private readonly ObjectStore _objects;
    private readonly IClock _clock;
    private readonly ResearchServices _services;
    private readonly SearchArtifacts _legacySearchArtifacts;

    public ResearchBroker(DataRoot root, ObjectStore objects, IClock clock, ResearchServices services)
    {
        _objects = objects;
        _clock = clock;
        _services = services;
        _legacySearchArtifacts = new SearchArtifacts(root, () => clock.UtcNow);
    }

    public bool SearchAvailable => _services.SearchAvailable;
    public bool DelegateAvailable => _services.DelegateAvailable;

    public bool Handles(string capability)
        => capability is ResearchCapabilities.Search
            or ResearchCapabilities.Delegate
            or ResearchCapabilities.ModelRequest
            or Actions.ModelRequest;

    public async Task<OperationApplyResult> ApplyAsync(OperationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        return envelope.Capability switch
        {
            ResearchCapabilities.Search => await RunSearchAsync(envelope, cancellationToken).ConfigureAwait(false),
            ResearchCapabilities.Delegate or ResearchCapabilities.ModelRequest or Actions.ModelRequest
                => await RunDelegateAsync(envelope, cancellationToken).ConfigureAwait(false),
            _ => new OperationApplyResult(false, "Not a research capability.", Error: envelope.Capability),
        };
    }

    private async Task<OperationApplyResult> RunSearchAsync(OperationEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_services.Search is null)
            return new OperationApplyResult(false, "No search adapter bound.", Error: "search_unavailable");

        var query = ReqString(envelope, "query");
        var limit = OptInt(envelope, "limit") ?? 5;
        var allowedHosts = OptStringList(envelope, "allowedHosts");

        var sw = Stopwatch.StartNew();
        var response = await _services.Search.SearchAsync(new SearchRequest(query, limit), cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (!response.Ok)
            return new OperationApplyResult(false, response.Error ?? "search failed", Error: response.Error);

        var hitArtifacts = new List<object>();
        var pageArtifacts = new List<object>();
        var artifactObjectIds = new List<string>();

        foreach (var hit in response.Hits)
        {
            // Always store the hit as a citable local artifact (ObjectStore + legacy search folder).
            var legacy = _legacySearchArtifacts.Store(query, hit, _services.Search.Host);
            var hitBlob = new
            {
                kind = "research.search_hit",
                artifactId = legacy.ArtifactId,
                query,
                title = hit.Title,
                url = hit.Url,
                snippet = hit.Snippet,
                host = _services.Search.Host,
                sha256 = legacy.Sha256,
                at = _clock.UtcNow,
            };
            var storedHit = _objects.PutJson(hitBlob);
            artifactObjectIds.Add(storedHit.ObjectId);
            hitArtifacts.Add(new
            {
                objectId = storedHit.ObjectId,
                sha256 = storedHit.Sha256,
                artifactId = legacy.ArtifactId,
                title = hit.Title,
                url = hit.Url,
                snippet = hit.Snippet,
            });

            if (_services.Fetch is not null && IsHostAllowed(hit.Url, allowedHosts, _services.Fetch.AllowedHosts))
            {
                var page = await _services.Fetch.FetchAsync(hit.Url, cancellationToken).ConfigureAwait(false);
                if (page.Ok && !string.IsNullOrWhiteSpace(page.Body))
                {
                    var pageBlob = new
                    {
                        kind = "research.page",
                        url = hit.Url,
                        title = hit.Title,
                        body = page.Body,
                        fetchedAt = _clock.UtcNow,
                        sourceHitObjectId = storedHit.ObjectId,
                    };
                    var storedPage = _objects.PutJson(pageBlob);
                    artifactObjectIds.Add(storedPage.ObjectId);
                    pageArtifacts.Add(new
                    {
                        objectId = storedPage.ObjectId,
                        sha256 = storedPage.Sha256,
                        url = hit.Url,
                        title = hit.Title,
                        chars = page.Body!.Length,
                    });
                }
            }
        }

        var package = new
        {
            kind = "research.search_result",
            query,
            allowedHosts,
            searchHost = _services.Search.Host,
            hitCount = response.Hits.Count,
            hits = hitArtifacts,
            pages = pageArtifacts,
            artifactObjectIds,
            elapsedMs = sw.ElapsedMilliseconds,
            at = _clock.UtcNow,
        };
        var stored = _objects.PutJson(package);
        return new OperationApplyResult(true, $"Search stored {artifactObjectIds.Count} artifact(s).", stored.ObjectId);
    }

    private async Task<OperationApplyResult> RunDelegateAsync(OperationEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_services.Delegate is null)
            return new OperationApplyResult(false, "No delegate adapter bound.", Error: "delegate_unavailable");

        var objective = ReqString(envelope, "objective");
        var profile = OptString(envelope, "profile") ?? _services.Delegate.Profile;
        var artifactIds = OptStringList(envelope, "artifactObjectIds");
        if (artifactIds.Count == 0 && envelope.Arguments.TryGetValue("artifactIds", out var alt) && alt.ValueKind == JsonValueKind.Array)
        {
            artifactIds = alt.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => s.Length > 0)
                .ToList();
        }

        var sources = new List<DelegateSource>();
        foreach (var id in artifactIds)
        {
            var text = TryReadObjectText(id);
            if (text is null) continue;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "artifact" : "artifact";
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
            var url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var sha = root.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            sources.Add(new DelegateSource(id, sha, kind, title, url));
        }

        var packageId = Ulid.NewUlid(_clock.UtcNow);
        var packageBlob = new
        {
            kind = "research.delegate_package",
            packageId,
            profile,
            objective,
            artifactObjectIds = artifactIds,
            sources = sources.Select(s => new { s.ObjectId, s.Sha256, s.Kind, s.Title, s.Url }),
            at = _clock.UtcNow,
            // Explicit: packages never include write capabilities.
            grants = Array.Empty<string>(),
        };
        var packageStored = _objects.PutJson(packageBlob);

        var request = new DelegateRequest(packageId, profile, objective, artifactIds, sources);
        var sw = Stopwatch.StartNew();
        var response = await _services.Delegate.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var responseBlob = new
        {
            kind = "research.delegate_response",
            packageId,
            packageObjectId = packageStored.ObjectId,
            packageSha256 = packageStored.Sha256,
            profile,
            ok = response.Ok,
            text = response.Text,
            error = response.Error,
            elapsedMs = response.ElapsedMs > 0 ? response.ElapsedMs : sw.ElapsedMilliseconds,
            citedArtifactObjectIds = artifactIds,
            at = _clock.UtcNow,
        };
        var responseStored = _objects.PutJson(responseBlob);

        if (!response.Ok)
            return new OperationApplyResult(false, response.Error ?? "delegate failed", responseStored.ObjectId, response.Error);

        return new OperationApplyResult(true, "Delegate response stored.", responseStored.ObjectId);
    }

    private string? TryReadObjectText(string objectId)
    {
        var metaPath = Path.Combine(_objects.ObjectsDirectory, "by-id", objectId + ".json");
        var meta = AtomicFile.ReadAllTextIfExists(metaPath);
        if (meta is null) return null;
        using var doc = JsonDocument.Parse(meta);
        if (!doc.RootElement.TryGetProperty("sha256", out var hash)) return null;
        return _objects.TryReadTextByHash(hash.GetString()!);
    }

    private static bool IsHostAllowed(string url, IReadOnlyList<string> grantedHosts, IReadOnlyList<string> adapterHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        var allowed = grantedHosts.Count > 0 ? grantedHosts : adapterHosts;
        return allowed.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReqString(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
            throw new InvalidOperationException($"Missing argument '{key}'.");
        return el.GetString()!;
    }

    private static string? OptString(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String) return null;
        return el.GetString();
    }

    private static int? OptInt(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el)) return null;
        if (el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var p)) return p;
        return null;
    }

    private static List<string> OptStringList(OperationEnvelope envelope, string key)
    {
        if (!envelope.Arguments.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
