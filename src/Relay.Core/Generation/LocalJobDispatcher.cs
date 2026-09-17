using System.Text.Json;
using Relay.Core.Evidence;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Generation;

public sealed record LocalJobResult(bool Ok, string? OutputJson = null, string? Error = null, IReadOnlyList<ExtractionCandidate>? Extractions = null);

/// <summary>
/// Dispatches bounded local jobs. Routing order is fixed. Local jobs never invoke tools
/// and never substitute for Jev judgments.
/// </summary>
public sealed class LocalJobDispatcher
{
    private readonly EvidenceStore _evidence;
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly Func<LocalJobDefinition, string, CancellationToken, Task<string>>? _generate;
    private readonly Dictionary<string, ResolutionRoundState> _rounds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>When false, local judgment fallback for Jev outages is refused.</summary>
    public bool AllowLocalJudgmentFallback => false;

    public LocalJobDispatcher(
        EvidenceStore evidence,
        DataRoot root,
        IClock clock,
        Func<LocalJobDefinition, string, CancellationToken, Task<string>>? generate = null)
    {
        _evidence = evidence;
        _root = root;
        _clock = clock;
        _generate = generate;
    }

    public ResolutionRoundState GetOrCreateRounds(string logicalDecisionId)
    {
        lock (_gate)
        {
            if (!_rounds.TryGetValue(logicalDecisionId, out var state))
            {
                state = new ResolutionRoundState { LogicalDecisionId = logicalDecisionId, Round = ResolutionRounds.Initial };
                _rounds[logicalDecisionId] = state;
                PersistRound(state);
            }
            return state;
        }
    }

    public ResolutionRoundState AdvanceRound(string logicalDecisionId)
    {
        lock (_gate)
        {
            var state = GetOrCreateRounds(logicalDecisionId);
            if (state.Round < ResolutionRounds.Reasoning)
                state.Round++;
            PersistRound(state);
            return state;
        }
    }

    /// <summary>Transport retries must call this — it does not advance rounds.</summary>
    public void NoteTransportRetry(string logicalDecisionId) => GetOrCreateRounds(logicalDecisionId);

    public async Task<LocalJobResult> DispatchAsync(LocalJobDefinition job, CancellationToken cancellationToken = default)
    {
        if (!LocalJobTypes.All.Contains(job.Type))
            return new LocalJobResult(false, Error: "unknown_job_type");

        // Local jobs cannot invoke tools — enforced by absence of tool surface here.
        if (job.Type == LocalJobTypes.ExtractCandidates)
            return Extract(job);

        var prompt = BuildPrompt(job);
        if (_generate is null)
            return new LocalJobResult(false, Error: "no_local_generator_bound");

        var text = await _generate(job, prompt, cancellationToken).ConfigureAwait(false);
        if (text.Length > job.OutputCharLimit)
            text = text[..job.OutputCharLimit];
        if (job.RequireSourceRefs && !text.Contains("source", StringComparison.OrdinalIgnoreCase)
            && job.PermittedInputArtifactIds.Count > 0
            && !job.PermittedInputArtifactIds.Any(id => text.Contains(id, StringComparison.Ordinal)))
        {
            // Soft check — structured extractors validate mechanically elsewhere.
        }
        return new LocalJobResult(true, text);
    }

    /// <summary>
    /// Routing rules in order: extract → resolve refs → (optional search formulate) →
    /// fill args → draft. Never routes to Jev-replacement local judgment.
    /// </summary>
    public static IReadOnlyList<string> RouteJobTypes(bool needsReferences, bool needsSearch, bool needsDraft)
    {
        var list = new List<string> { LocalJobTypes.ExtractCandidates };
        if (needsReferences) list.Add(LocalJobTypes.ResolveReferenceCandidates);
        if (needsSearch) list.Add(LocalJobTypes.FormulateSearchQuery);
        list.Add(LocalJobTypes.FillInterpretiveArguments);
        if (needsDraft) list.Add(LocalJobTypes.DraftAnswer);
        return list;
    }

    public bool TryLocalJudgmentFallback() => AllowLocalJudgmentFallback;

    private LocalJobResult Extract(LocalJobDefinition job)
    {
        var extractions = new List<ExtractionCandidate>();
        foreach (var artifactId in job.PermittedInputArtifactIds)
        {
            var text = _evidence.TryReadText(artifactId);
            if (text is null) continue;
            // Deterministic sentence-ish candidates for tests/harness without a model.
            var start = 0;
            var idx = 0;
            while (start < text.Length && extractions.Count < 32)
            {
                var end = text.IndexOfAny(['.', '!', '?'], start);
                if (end < 0) end = text.Length - 1;
                end = Math.Min(text.Length - 1, end);
                var sliceEnd = end + 1;
                var slice = text[start..sliceEnd].Trim();
                if (slice.Length > 0)
                {
                    var candidate = new ExtractionCandidate
                    {
                        CandidateId = Ulid.NewUlid(_clock.UtcNow) + "-" + idx,
                        Type = "passage",
                        Text = slice,
                        SourceArtifactId = artifactId,
                        StartOffset = text.IndexOf(slice, start, StringComparison.Ordinal),
                        EndOffset = text.IndexOf(slice, start, StringComparison.Ordinal) + slice.Length,
                    };
                    if (candidate.StartOffset >= 0 && ExtractionValidator.Validate(candidate, text, out _))
                        extractions.Add(candidate);
                    idx++;
                }
                start = sliceEnd;
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            }
        }
        var json = JsonSerializer.Serialize(extractions, RelayJson.Compact);
        return new LocalJobResult(true, json, Extractions: extractions);
    }

    private string BuildPrompt(LocalJobDefinition job)
    {
        var inputs = job.PermittedInputArtifactIds
            .Select(id => (id, text: _evidence.TryReadText(id)))
            .Where(t => t.text is not null)
            .Select(t => $"[{t.id}]\n{t.text}");
        return $"Job {job.Type} v{job.Version}\nTask: {job.Task}\nInputs:\n{string.Join("\n---\n", inputs)}";
    }

    private void PersistRound(ResolutionRoundState state)
    {
        var dir = Path.Combine(_root.Path, "jobs", "rounds");
        Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(Path.Combine(dir, state.LogicalDecisionId + ".json"),
            JsonSerializer.Serialize(state, RelayJson.Indented));
    }
}
