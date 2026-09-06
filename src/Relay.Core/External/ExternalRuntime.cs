using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Config;
using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Model;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.External;

/// <summary>What was sent to an external model, verbatim. Stored beside the artifact so the user can see exactly what left the machine.</summary>
public sealed record ExternalPackage(string PackageId, string Profile, string Objective, IReadOnlyList<string> Refs, IReadOnlyList<PackagedSource> Sources, int Chars, string Sha256, DateTimeOffset At);

public sealed record PackagedSource(string Id, string Kind, string Text);

public sealed record ExternalArtifact(string ArtifactId, string PackageId, string Profile, string Model, string Host, int PromptTokens, int CompletionTokens, long ElapsedMs, string Text, string Sha256, DateTimeOffset At);

/// <summary>
/// Runs approved <c>model.request</c> operations. Builds the package from ids the planner cited (only
/// ids a read-only tool returned; nothing else can be packaged), writes it to disk before sending so
/// the record exists even if the process dies mid-request, calls the profile's https client off the
/// coordinator thread, stores the response as an artifact, and completes the pending operation with the
/// artifact id. Every step is a ledger record; the package and artifact files are the full text.
/// </summary>
public sealed class ExternalRuntime : IExternalOperations
{
    private sealed record InFlight(Proposal Proposal, string TaskId, IExecutionSink Sink, ExternalPackage Package, ExternalModelProfile Profile, CancellationTokenSource Cts);

    private readonly DataRoot _root;
    private readonly IReadOnlyList<ExternalModelProfile> _profiles;
    private readonly Func<ExternalModelProfile, IModelClient> _clientFactory;
    private readonly Func<ToolSources> _sources;
    private readonly IScheduler _scheduler;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);

    public ExternalRuntime(DataRoot root, IReadOnlyList<ExternalModelProfile> profiles, Func<ExternalModelProfile, IModelClient> clientFactory, Func<ToolSources> sources, IScheduler scheduler, Func<DateTimeOffset>? clock = null)
    {
        _root = root;
        _profiles = profiles;
        _clientFactory = clientFactory;
        _sources = sources;
        _scheduler = scheduler;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_root.ExternalArtifactsDirectory);
    }

    /// <summary>Set by the composition root: (proposalId, result) â†’ coordinator.CompletePendingOperation. Invoked on the coordinator thread.</summary>
    public Action<string, ExecutionResult>? Completed { get; set; }

    public IReadOnlyList<string> ProfileNames => _profiles.Select(p => p.Name).ToList();

    public string? ReadArtifact(string artifactId)
    {
        if (!Ulid.IsValid(artifactId)) return null;
        var path = ArtifactPath(artifactId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ExternalArtifact>(File.ReadAllText(path), RelayJson.Indented)?.Text; }
        catch (JsonException) { return null; }
    }

    public IEnumerable<(string Id, string Text, DateTimeOffset At)> AllArtifacts()
    {
        if (!Directory.Exists(_root.ExternalArtifactsDirectory)) yield break;
        foreach (var file in Directory.EnumerateFiles(_root.ExternalArtifactsDirectory, "*.artifact.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            ExternalArtifact? artifact = null;
            try { artifact = JsonSerializer.Deserialize<ExternalArtifact>(File.ReadAllText(file), RelayJson.Indented); } catch (JsonException) { }
            if (artifact is not null) yield return (artifact.ArtifactId, artifact.Text, artifact.At);
        }
    }

    public ExternalPackage? ReadPackage(string packageId)
    {
        var path = Path.Combine(_root.ExternalArtifactsDirectory, packageId + ".package.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ExternalPackage>(File.ReadAllText(path), RelayJson.Indented); } catch (JsonException) { return null; }
    }

    /// <summary>Executor entry point. Packages, records, sends off-thread, and returns Pending; the completion arrives through <see cref="Completed"/>.</summary>
    public ExecutionResult Request(Proposal proposal, Decision decision, string taskId, IExecutionSink sink)
    {
        var target = decision.NormalizedTarget.Count > 0 ? decision.NormalizedTarget : proposal.Target;
        var profileName = target.GetValueOrDefault("profile") ?? "";
        var objective = target.GetValueOrDefault("objective") ?? "";
        var refs = (target.GetValueOrDefault("refs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var budget = int.TryParse(target.GetValueOrDefault("budgetTokens"), out var b) ? b : 0;
        var allowSearch = string.Equals(target.GetValueOrDefault("allowSearch"), "true", StringComparison.OrdinalIgnoreCase);

        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.Ordinal));
        if (profile is null) return ExecutionResult.Fail($"No external profile named '{profileName}'.");
        if (allowSearch && !profile.SupportsSearch) return ExecutionResult.Fail($"Profile '{profileName}' does not support online search.");
        if (_inFlight.ContainsKey(proposal.ProposalId)) return ExecutionResult.Fail("That request is already in flight.");

        IModelClient client;
        try { client = _clientFactory(profile); }
        catch (ArgumentException ex) { return ExecutionResult.Fail("Profile is misconfigured: " + ex.Message); }

        var package = BuildPackage(profile, objective, refs, _clock());
        AtomicFile.WriteAllText(Path.Combine(_root.ExternalArtifactsDirectory, package.PackageId + ".package.json"), JsonSerializer.Serialize(package, RelayJson.Indented));
        sink.Record(EventTypes.ExternalPackaged, new { taskId, proposalId = proposal.ProposalId, packageId = package.PackageId, profile = profile.Name, model = profile.Model, endpoint = profile.Endpoint, refs = package.Refs, sources = package.Sources.Count, chars = package.Chars, sha256 = package.Sha256, allowSearch });

        var messages = new List<ModelMessage>
        {
            new("system", "You are a capable assistant working on a bounded task for a local orchestrator. Use only the sources provided unless told you may search. Answer in plain prose, cite sources by their id in square brackets, and state clearly what you could not determine."),
            new("user", UserMessage(package, allowSearch)),
        };
        var maxTokens = Math.Clamp(budget > 0 ? budget : profile.MaxOutputTokens, 100, profile.MaxOutputTokens);
        var flight = new InFlight(proposal, taskId, sink, package, profile, new CancellationTokenSource());
        _inFlight[proposal.ProposalId] = flight;

        var request = new ModelRequest(profile.Model, messages, maxTokens, JsonObject: false);
        Task.Run(async () =>
        {
            ModelResponse response;
            try { response = await client.CompleteAsync(request, flight.Cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { response = ModelResponse.Failed("cancelled", 0); }
            finally { (client as IDisposable)?.Dispose(); }
            _scheduler.Post(() => Finish(flight, response));
        });

        return ExecutionResult.Pending($"Package {package.PackageId[^8..]} ({package.Sources.Count} source(s), {package.Chars} chars) sent to {profile.Name} ({profile.Model})",
            new Dictionary<string, string> { ["packageId"] = package.PackageId, ["profile"] = profile.Name, ["model"] = profile.Model, ["chars"] = package.Chars.ToString() });
    }

    /// <summary>Cancels an in-flight request; the completion arrives as a failure so the task can close.</summary>
    public bool Stop(string? proposalId, string reason)
    {
        if (proposalId is null || !_inFlight.TryGetValue(proposalId, out var flight)) return false;
        flight.Cts.Cancel();
        return true;
    }

    private void Finish(InFlight flight, ModelResponse response)
    {
        if (!_inFlight.Remove(flight.Proposal.ProposalId)) return;
        var sink = flight.Sink;
        var package = flight.Package;
        var profile = flight.Profile;
        var taskId = flight.TaskId;
        sink.Record(EventTypes.ExternalResponded, new { taskId, proposalId = flight.Proposal.ProposalId, packageId = package.PackageId, ok = response.Ok, error = response.Error, chars = response.Content?.Length ?? 0, promptTokens = response.PromptTokens, completionTokens = response.CompletionTokens, elapsedMs = response.ElapsedMs });
        ExecutionResult result;
        if (!response.Ok) result = ExecutionResult.Fail(response.Error ?? "external model failed");
        else
        {
            var text = response.Content ?? "";
            var artifact = new ExternalArtifact(Ulid.NewUlid(_clock()), package.PackageId, profile.Name, profile.Model, profile.Endpoint, response.PromptTokens, response.CompletionTokens, response.ElapsedMs, text, Sha(text), _clock());
            try
            {
                AtomicFile.WriteAllText(ArtifactPath(artifact.ArtifactId), JsonSerializer.Serialize(artifact, RelayJson.Indented));
                sink.Record(EventTypes.ArtifactStored, new { taskId, artifactId = artifact.ArtifactId, packageId = package.PackageId, profile = profile.Name, chars = text.Length, sha256 = artifact.Sha256 });
                _sources().Index.IndexArtifact(artifact.ArtifactId, text, artifact.At);
                result = ExecutionResult.Ok($"{profile.Name} ({profile.Model}) answered in {response.ElapsedMs} ms; {text.Length} chars stored as artifact {artifact.ArtifactId}", new Dictionary<string, string>
                {
                    ["artifactId"] = artifact.ArtifactId,
                    ["packageId"] = package.PackageId,
                    ["profile"] = profile.Name,
                    ["model"] = profile.Model,
                    ["promptTokens"] = response.PromptTokens.ToString(),
                    ["completionTokens"] = response.CompletionTokens.ToString(),
                    ["chars"] = text.Length.ToString(),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = ExecutionResult.Fail("The response arrived but could not be stored: " + ex.Message);
            }
        }
        flight.Cts.Dispose();
        Completed?.Invoke(flight.Proposal.ProposalId, result);
    }

    private ExternalPackage BuildPackage(ExternalModelProfile profile, string objective, IReadOnlyList<string> refs, DateTimeOffset now)
    {
        var sources = new List<PackagedSource>();
        var tools = _sources();
        foreach (var id in refs.Distinct(StringComparer.Ordinal))
        {
            var text = Resolve(tools, id, out var kind);
            if (text is not null) sources.Add(new PackagedSource(id, kind, text.Length > 20_000 ? text[..20_000] + "â€¦(truncated)" : text));
        }
        var chars = objective.Length + sources.Sum(s => s.Text.Length);
        var digest = Sha(objective + "\n" + string.Join("\n", sources.Select(s => s.Id + ":" + s.Text)));
        return new ExternalPackage(Ulid.NewUlid(now), profile.Name, objective, refs, sources, chars, digest, now);
    }

    private static string? Resolve(ToolSources tools, string id, out string kind)
    {
        kind = "unknown";
        if (tools.Excerpts?.Read(id) is { } excerpt) { kind = "excerpt"; return excerpt.Text; }
        if (tools.ReadArtifact?.Invoke(id) is { } artifact) { kind = "artifact"; return artifact; }
        foreach (var project in tools.Registry.Active)
        {
            if (!Directory.Exists(project.RootPath)) continue;
            var found = Notes.ProjectNoteStore.Find(project.RootPath, id);
            if (found is not null) { kind = "note"; return $"[{project.Slug}/{found.Value.Note.Type}] " + found.Value.Note.Body; }
        }
        var draft = tools.Drafts.Unrouted().FirstOrDefault(d => d.NoteId == id);
        if (draft is not null) { kind = "draft"; return draft.Text; }
        var hit = tools.Index.Search(id, null, 1).FirstOrDefault(h => h.Id == id);
        if (hit is not null) { kind = hit.Kind; return hit.Text; }
        return null;
    }

    private static string UserMessage(ExternalPackage package, bool allowSearch)
    {
        var sb = new StringBuilder();
        sb.Append("Objective:\n").Append(package.Objective).Append("\n\n");
        if (package.Sources.Count > 0)
        {
            sb.Append("Sources (cite by id):\n");
            foreach (var s in package.Sources) sb.Append("--- [").Append(s.Id).Append("] (").Append(s.Kind).Append(")\n").Append(s.Text).Append('\n');
        }
        else sb.Append("No local sources were provided.\n");
        sb.Append(allowSearch ? "\nYou may search online; name what you searched and what you relied on." : "\nDo not use anything beyond the sources above and general knowledge; say so when the sources do not answer the objective.");
        return sb.ToString();
    }

    private string ArtifactPath(string artifactId) => Path.Combine(_root.ExternalArtifactsDirectory, artifactId + ".artifact.json");

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
