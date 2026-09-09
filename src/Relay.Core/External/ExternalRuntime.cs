using System.Diagnostics;
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

/// <summary>
/// What was sent to an external model, verbatim. Stored beside the artifact so the user can see exactly what left the
/// machine. A follow-up turn of a conversation (slice 5) is its own package: <see cref="ConversationId"/> is the first
/// package's proposal id and <see cref="Turn"/> counts from 1; the sources of turn 1 are not repeated, they are in the
/// conversation the model already holds.
/// </summary>
public sealed record ExternalPackage(string PackageId, string Profile, string Objective, IReadOnlyList<string> Refs, IReadOnlyList<PackagedSource> Sources, int Chars, string Sha256, DateTimeOffset At, string? ConversationId = null, int Turn = 1);

public sealed record PackagedSource(string Id, string Kind, string Text);

/// <summary>One reply, whole, with the digest the local model made of it (≤3 lines; <see cref="DigestBy"/> is null when the lines are the reply's own first lines).</summary>
public sealed record ExternalArtifact(string ArtifactId, string PackageId, string Profile, string Model, string Host, int PromptTokens, int CompletionTokens, long ElapsedMs, string Text, string Sha256, DateTimeOffset At,
    string? ConversationId = null, int Turn = 1, IReadOnlyList<string>? Digest = null, string? DigestBy = null);

/// <summary>
/// Runs approved <c>model.request</c> operations. Builds the package from ids the planner cited (only
/// ids a read-only tool returned; nothing else can be packaged), writes it to disk before sending so
/// the record exists even if the process dies mid-request, calls the profile's https client off the
/// coordinator thread, stores the response as an artifact, and completes the pending operation with the
/// artifact id. Every step is a ledger record; the package and artifact files are the full text.
/// <para>
/// Slice 5: replies stream (partials reach the loop through <see cref="Progress"/>), every reply is digested
/// into feed lines by the local model (<see cref="Digester"/>, the <c>digest.md</c> prompt), and a request may
/// be continued: a target with <c>conversation</c> set to an earlier request's proposal id sends its objective
/// as the next user turn of that conversation, under the first request's approval, for at most
/// <see cref="MaxTurns"/> turns and with no new local sources. Conversations live in memory: after a restart the
/// artifacts remain and a follow-up is a new request.
/// </para>
/// </summary>
public sealed class ExternalRuntime : IExternalOperations
{
    private sealed record InFlight(Proposal Proposal, string TaskId, IExecutionSink Sink, ExternalPackage Package, ExternalModelProfile Profile, CancellationTokenSource Cts, Conversation Conversation, string Objective);

    /// <summary>One approved exchange with a delegate and every turn so far; <see cref="Messages"/> is what the model sees, whole.</summary>
    private sealed class Conversation
    {
        public required string ConversationId { get; init; }
        public required string TaskId { get; init; }
        public required ExternalModelProfile Profile { get; init; }
        public required IReadOnlyList<string> Refs { get; init; }
        public required int Budget { get; init; }
        public required bool AllowSearch { get; init; }
        public List<ModelMessage> Messages { get; } = [];
        /// <summary>Turns that have returned.</summary>
        public int Turns { get; set; }
        public bool Open { get; set; }
    }

    public const int DefaultMaxTurns = 3;

    private readonly DataRoot _root;
    private readonly IReadOnlyList<ExternalModelProfile> _profiles;
    private readonly Func<ExternalModelProfile, IModelClient> _clientFactory;
    private readonly Func<ToolSources> _sources;
    private readonly IScheduler _scheduler;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

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

    /// <summary>Set by the composition root: (proposalId, result) -> coordinator.CompletePendingOperation. Invoked on the coordinator thread.</summary>
    public Action<string, ExecutionResult>? Completed { get; set; }

    /// <summary>
    /// Set by the composition root: (proposalId, taskId, chars so far, tail of the text) while a reply streams in, on the
    /// coordinator thread, at most every <see cref="ProgressEveryChars"/> characters or <see cref="ProgressEvery"/>. The
    /// loop decides (the narrate weights) whether a given progress report is worth a step of the mind.
    /// </summary>
    public Action<string, string, int, string>? Progress { get; set; }
    public int ProgressEveryChars { get; set; } = 120;
    public TimeSpan ProgressEvery { get; set; } = TimeSpan.FromSeconds(1);
    private const int TailChars = 240;

    /// <summary>Set by the composition root: the local model that digests replies (null when it is off; the digest is then the reply's first lines).</summary>
    public Func<IModelClient?>? Digester { get; set; }
    /// <summary>Set by the composition root: the <c>digest.md</c> override, if the user keeps one.</summary>
    public Func<string?>? DigestInstructions { get; set; }
    /// <summary>How many turns one approval covers (the first exchange counts as one). Bounded multi-turn, first cut.</summary>
    public int MaxTurns { get; set; } = DefaultMaxTurns;

    public IReadOnlyList<string> ProfileNames => _profiles.Select(p => p.Name).ToList();
    public IReadOnlyList<string> SearchProfileNames => _profiles.Where(p => p.SupportsSearch).Select(p => p.Name).ToList();

    public string? ReadArtifact(string artifactId) => ReadArtifactRecord(artifactId)?.Text;

    public ExternalArtifact? ReadArtifactRecord(string artifactId)
    {
        if (!Ulid.IsValid(artifactId)) return null;
        var path = ArtifactPath(artifactId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ExternalArtifact>(File.ReadAllText(path), RelayJson.Indented); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether a follow-up turn may be sent in the conversation that began with <paramref name="conversationId"/>, and if not, why.
    /// Checked by the coordinator before it runs the turn under the first approval; the same checks guard <see cref="Request"/>.
    /// </summary>
    public (bool Ok, string Reason, int Turn, int TurnsLeft) CanContinue(string conversationId, string taskId, IReadOnlyList<string> refs)
    {
        if (!_conversations.TryGetValue(conversationId, out var c))
            return (false, $"Request {conversationId} is not a conversation this runtime knows (it never ran, or Relay restarted since); a follow-up is a new delegate request.", 0, 0);
        if (!string.Equals(c.TaskId, taskId, StringComparison.Ordinal))
            return (false, $"Request {conversationId} belongs to another task; a follow-up is a new delegate request.", c.Turns, 0);
        // The bound is named before the closed state it caused: a spent conversation is told so, not "not open".
        if (c.Turns >= MaxTurns)
            return (false, $"The conversation with {c.Profile.Name} has used its {MaxTurns} turns; anything further is a new delegate request the user approves.", c.Turns, 0);
        if (_inFlight.Values.Any(f => f.Conversation == c))
            return (false, $"Request {conversationId} is still answering; wait for its reply.", c.Turns, Math.Max(0, MaxTurns - c.Turns));
        if (!c.Open)
            return (false, $"Request {conversationId} is not an open conversation (it failed, or was stopped); a follow-up is a new delegate request.", c.Turns, 0);
        var extra = refs.Where(r => !c.Refs.Contains(r, StringComparer.Ordinal)).ToList();
        if (extra.Count > 0)
            return (false, $"A follow-up may not add local sources ({string.Join(", ", extra)}) — the approval covered {(c.Refs.Count == 0 ? "none" : string.Join(", ", c.Refs))}. Make a new delegate request with those refs, which the user approves.", c.Turns, MaxTurns - c.Turns);
        return (true, $"turn {c.Turns + 1} of {MaxTurns} in the conversation the user approved", c.Turns + 1, MaxTurns - c.Turns - 1);
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

        var conversationId = target.GetValueOrDefault("conversation");

        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.Ordinal));
        if (profile is null) return ExecutionResult.Fail($"No external profile named '{profileName}'.");
        if (allowSearch && !profile.SupportsSearch) return ExecutionResult.Fail($"Profile '{profileName}' does not support online search.");
        if (_inFlight.ContainsKey(proposal.ProposalId)) return ExecutionResult.Fail("That request is already in flight.");

        Conversation conversation;
        int turn;
        if (conversationId is not null)
        {
            // A follow-up turn: the first request's approval covers it, within the bounds CanContinue states.
            var (ok, reason, nextTurn, _) = CanContinue(conversationId, taskId, refs);
            if (!ok) return ExecutionResult.Fail(reason);
            conversation = _conversations[conversationId];
            profile = conversation.Profile;
            allowSearch = conversation.AllowSearch;
            budget = conversation.Budget;
            turn = nextTurn;
        }
        else
        {
            conversation = new Conversation { ConversationId = proposal.ProposalId, TaskId = taskId, Profile = profile, Refs = refs, Budget = budget, AllowSearch = allowSearch };
            turn = 1;
        }

        IModelClient client;
        try { client = _clientFactory(profile); }
        catch (ArgumentException ex) { return ExecutionResult.Fail("Profile is misconfigured: " + ex.Message); }

        var package = turn == 1
            ? BuildPackage(profile, objective, refs, _clock())
            : new ExternalPackage(Ulid.NewUlid(_clock()), profile.Name, objective, [], [], objective.Length, Sha(objective), _clock(), conversation.ConversationId, turn);
        AtomicFile.WriteAllText(Path.Combine(_root.ExternalArtifactsDirectory, package.PackageId + ".package.json"), JsonSerializer.Serialize(package, RelayJson.Indented));
        sink.Record(EventTypes.ExternalPackaged, new { taskId, proposalId = proposal.ProposalId, packageId = package.PackageId, profile = profile.Name, model = profile.Model, endpoint = profile.Endpoint, refs = package.Refs, sources = package.Sources.Count, chars = package.Chars, sha256 = package.Sha256, allowSearch, conversationId = conversation.ConversationId, turn, maxTurns = MaxTurns });

        List<ModelMessage> messages;
        if (turn == 1)
        {
            conversation.Messages.Add(new("system", "You are a capable assistant working on a bounded task for a local orchestrator. Use only the sources provided unless told you may search. Answer in plain prose, cite sources by their id in square brackets, and state clearly what you could not determine. The orchestrator may follow up with questions in this conversation."));
            conversation.Messages.Add(new("user", UserMessage(package, allowSearch)));
        }
        else conversation.Messages.Add(new("user", objective));
        messages = [.. conversation.Messages];
        var maxTokens = Math.Clamp(budget > 0 ? budget : profile.MaxOutputTokens, 100, profile.MaxOutputTokens);
        var flight = new InFlight(proposal, taskId, sink, package, profile, new CancellationTokenSource(), conversation, objective);
        _inFlight[proposal.ProposalId] = flight;

        var request = new ModelRequest(profile.Model, messages, maxTokens, JsonObject: false);
        var proposalId = proposal.ProposalId;
        var digester = Digester;
        var digestInstructions = DigestInstructions;
        Task.Run(async () =>
        {
            ModelResponse response;
            var streamed = new StringBuilder();
            var reportedChars = 0;
            var reportedAt = Stopwatch.StartNew();
            try
            {
                response = await client.StreamAsync(request, (delta, _) =>
                {
                    streamed.Append(delta);
                    if (Progress is null) return Task.CompletedTask;
                    if (streamed.Length - reportedChars < ProgressEveryChars && reportedAt.Elapsed < ProgressEvery) return Task.CompletedTask;
                    reportedChars = streamed.Length;
                    reportedAt.Restart();
                    var chars = streamed.Length;
                    var tail = chars <= TailChars ? streamed.ToString() : streamed.ToString(chars - TailChars, TailChars);
                    _scheduler.Post(() => Progress?.Invoke(proposalId, taskId, chars, tail));
                    return Task.CompletedTask;
                }, flight.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { response = ModelResponse.Failed("cancelled", 0); }
            finally { (client as IDisposable)?.Dispose(); }

            // The digest is made here, off the coordinator thread, so the reply arrives with its feed lines in one completion.
            // The digester is the local model's client, owned by whoever built it; it is not disposed here.
            var digest = DigestResult.None;
            if (response.Ok && !flight.Cts.IsCancellationRequested)
            {
                IModelClient? local = null;
                try { local = digester?.Invoke(); } catch (ArgumentException) { local = null; }
                try { digest = await Digest.MakeAsync(local, digestInstructions?.Invoke(), objective, response.Content ?? "", flight.Cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { digest = DigestResult.None; }
            }
            _scheduler.Post(() => Finish(flight, response, digest));
        });

        return ExecutionResult.Pending(turn == 1
                ? $"Package {package.PackageId[^8..]} ({package.Sources.Count} source(s), {package.Chars} chars) sent to {profile.Name} ({profile.Model})"
                : $"Turn {turn} of {MaxTurns} ({package.Chars} chars) sent to {profile.Name} ({profile.Model}) in conversation {conversation.ConversationId[^8..]}",
            new Dictionary<string, string> { ["packageId"] = package.PackageId, ["profile"] = profile.Name, ["model"] = profile.Model, ["chars"] = package.Chars.ToString(), ["conversationId"] = conversation.ConversationId, ["turn"] = turn.ToString(), ["maxTurns"] = MaxTurns.ToString() });
    }

    /// <summary>Cancels an in-flight request; the completion arrives as a failure so the task can close.</summary>
    public bool Stop(string? proposalId, string reason)
    {
        if (proposalId is null || !_inFlight.TryGetValue(proposalId, out var flight)) return false;
        flight.Cts.Cancel();
        return true;
    }

    private void Finish(InFlight flight, ModelResponse response, DigestResult digest)
    {
        if (!_inFlight.Remove(flight.Proposal.ProposalId)) return;
        var sink = flight.Sink;
        var package = flight.Package;
        var profile = flight.Profile;
        var taskId = flight.TaskId;
        var conversation = flight.Conversation;
        var turn = package.Turn;
        sink.Record(EventTypes.ExternalResponded, new { taskId, proposalId = flight.Proposal.ProposalId, packageId = package.PackageId, ok = response.Ok, error = response.Error, chars = response.Content?.Length ?? 0, promptTokens = response.PromptTokens, completionTokens = response.CompletionTokens, elapsedMs = response.ElapsedMs, conversationId = conversation.ConversationId, turn });
        ExecutionResult result;
        if (!response.Ok)
        {
            // A failed or stopped turn closes the conversation: the mind's next delegate is a new request the user approves.
            Close(conversation);
            result = ExecutionResult.Fail(response.Error ?? "external model failed");
        }
        else
        {
            var text = response.Content ?? "";
            var artifact = new ExternalArtifact(Ulid.NewUlid(_clock()), package.PackageId, profile.Name, profile.Model, profile.Endpoint, response.PromptTokens, response.CompletionTokens, response.ElapsedMs, text, Sha(text), _clock(),
                conversation.ConversationId, turn, digest.Ok ? digest.Lines : null, digest.Digester);
            try
            {
                AtomicFile.WriteAllText(ArtifactPath(artifact.ArtifactId), JsonSerializer.Serialize(artifact, RelayJson.Indented));
                sink.Record(EventTypes.ArtifactStored, new { taskId, artifactId = artifact.ArtifactId, packageId = package.PackageId, profile = profile.Name, chars = text.Length, sha256 = artifact.Sha256, conversationId = conversation.ConversationId, turn });
                _sources().Index.IndexArtifact(artifact.ArtifactId, text, artifact.At);
                conversation.Messages.Add(new("assistant", text));
                conversation.Turns = turn;
                conversation.Open = true;
                _conversations[conversation.ConversationId] = conversation;
                if (turn >= MaxTurns) Close(conversation);
                var turnsLeft = Math.Max(0, MaxTurns - turn);
                result = ExecutionResult.Ok($"{profile.Name} ({profile.Model}) answered in {response.ElapsedMs} ms; {text.Length} chars stored as artifact {artifact.ArtifactId}" + (turn > 1 || turnsLeft > 0 ? $" (turn {turn} of {MaxTurns})" : ""), new Dictionary<string, string>
                {
                    ["artifactId"] = artifact.ArtifactId,
                    ["packageId"] = package.PackageId,
                    ["profile"] = profile.Name,
                    ["model"] = profile.Model,
                    ["promptTokens"] = response.PromptTokens.ToString(),
                    ["completionTokens"] = response.CompletionTokens.ToString(),
                    ["chars"] = text.Length.ToString(),
                    ["conversationId"] = conversation.ConversationId,
                    ["turn"] = turn.ToString(),
                    ["turnsLeft"] = turnsLeft.ToString(),
                    ["digestLines"] = digest.Lines.Count.ToString(),
                    ["digestBy"] = digest.Digester ?? "",
                    ["digestTokens"] = (digest.PromptTokens + digest.CompletionTokens).ToString(),
                    ["digestError"] = digest.Error ?? "",
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Close(conversation);
                result = ExecutionResult.Fail("The response arrived but could not be stored: " + ex.Message);
            }
        }
        flight.Cts.Dispose();
        Completed?.Invoke(flight.Proposal.ProposalId, result);
    }

    /// <summary>
    /// A closed conversation stays on record so a <c>reply_to</c> naming it is refused with the reason (spent, failed, stopped)
    /// rather than as unknown; its messages go, so memory is bounded by the conversations still open.
    /// </summary>
    private void Close(Conversation conversation)
    {
        conversation.Open = false;
        conversation.Messages.Clear();
        _conversations[conversation.ConversationId] = conversation;
    }

    private ExternalPackage BuildPackage(ExternalModelProfile profile, string objective, IReadOnlyList<string> refs, DateTimeOffset now)
    {
        var sources = new List<PackagedSource>();
        var tools = _sources();
        foreach (var id in refs.Distinct(StringComparer.Ordinal))
        {
            var text = Resolve(tools, id, out var kind);
            if (text is not null) sources.Add(new PackagedSource(id, kind, text.Length > 20_000 ? text[..20_000] + " ...(truncated)" : text));
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
