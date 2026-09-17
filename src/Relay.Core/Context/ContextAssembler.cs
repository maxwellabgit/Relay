using System.Text;
using System.Text.Json;
using Relay.Core.Evidence;
using Relay.Core.Ids;
using Relay.Core.Judgments;
using Relay.Core.Time;

namespace Relay.Core.Context;

public sealed record ContextExcerpt(
    string ArtifactId,
    string Text,
    IReadOnlyList<string> SourceRefs,
    double? RelevanceScore = null,
    bool Mandatory = false,
    bool IsContradictionPair = false);

public sealed record AssembledContext(
    JsonElement State,
    ContextManifest Manifest,
    IReadOnlyList<ContextExcerpt> Excerpts);

/// <summary>
/// Follows IDs → candidates → restrictions → optional Jev relevance → assemble state.
/// Records inclusions, omissions, and truncation. Never silently truncates mandatory content.
/// </summary>
public sealed class ContextAssembler
{
    private readonly EvidenceStore _evidence;
    private readonly ProvenanceGraph _provenance;
    private readonly IClock _clock;

    public ContextAssembler(EvidenceStore evidence, ProvenanceGraph provenance, IClock clock)
    {
        _evidence = evidence;
        _provenance = provenance;
        _clock = clock;
    }

    public AssembledContext Assemble(
        string? caseId,
        string? decisionId,
        IReadOnlyList<string> seedArtifactIds,
        IReadOnlyList<ContextExcerpt>? rankedExcerpts = null,
        bool forHosted = false)
    {
        var manifest = new ContextManifest
        {
            ManifestId = Ulid.NewUlid(_clock.UtcNow),
            CaseId = caseId,
            DecisionId = decisionId,
        };

        var candidates = new List<ContentArtifact>();
        foreach (var id in seedArtifactIds.Distinct(StringComparer.Ordinal))
        {
            foreach (var lid in _provenance.LineageClosure(id))
            {
                var a = _evidence.TryLoad(lid);
                if (a is not null) candidates.Add(a);
            }
        }
        candidates = ContextRetrievalPolicy.CapCandidates(candidates.DistinctBy(c => c.ArtifactId)).ToList();
        manifest.CandidateCount = candidates.Count;

        if (forHosted)
        {
            var filtered = ContextRetrievalPolicy.FilterForHosted(candidates);
            foreach (var dropped in candidates.Where(c => filtered.All(f => f.ArtifactId != c.ArtifactId)))
            {
                manifest.OmittedArtifactIds.Add(dropped.ArtifactId);
                manifest.Notes.Add(ContextBlockReasons.RestrictedExcluded + ":" + dropped.ArtifactId);
            }
            candidates = filtered.ToList();
        }

        var rawExcerpts = rankedExcerpts is { Count: > 0 }
            ? rankedExcerpts.ToList()
            : candidates.Select(c => new ContextExcerpt(
                c.ArtifactId,
                _evidence.TryReadText(c.ArtifactId) ?? "",
                c.SourceRefs,
                Mandatory: false)).ToList();

        // Preserve primary passages / contradiction pairs before capping ranked excerpts.
        var excerpts = rawExcerpts
            .OrderByDescending(e => e.IsContradictionPair)
            .ThenByDescending(e => e.Mandatory)
            .ThenByDescending(e => e.RelevanceScore ?? 0)
            .Take(ContextRetrievalPolicy.MaxRankedExcerpts)
            .ToList();
        manifest.RankedExcerptCount = excerpts.Count;

        var sb = new StringBuilder();
        var included = new List<string>();
        var mandatorySkipped = new List<ContextExcerpt>();

        foreach (var excerpt in excerpts)
        {
            var chunk = excerpt.Text ?? "";
            if (sb.Length + chunk.Length + 1 > ContextRetrievalPolicy.MaxStateChars)
            {
                if (excerpt.Mandatory || excerpt.IsContradictionPair)
                {
                    mandatorySkipped.Add(excerpt);
                    continue;
                }
                manifest.TruncatedArtifactIds.Add(excerpt.ArtifactId);
                manifest.OmittedArtifactIds.Add(excerpt.ArtifactId);
                continue;
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(chunk);
            included.Add(excerpt.ArtifactId);
            manifest.IncludedArtifactIds.Add(excerpt.ArtifactId);
        }

        if (mandatorySkipped.Count > 0)
        {
            // Mandatory content does not fit — split or block; never silent truncate.
            manifest.SplitRequired = true;
            manifest.BlockReason = ContextBlockReasons.ContextTooLarge;
            foreach (var m in mandatorySkipped)
                manifest.OmittedArtifactIds.Add(m.ArtifactId);
            manifest.Notes.Add("mandatory_content_requires_split");
        }

        manifest.StateCharCount = sb.Length;
        var state = JsonSerializer.SerializeToElement(new
        {
            caseId,
            decisionId,
            excerpts = excerpts.Where(e => included.Contains(e.ArtifactId)).Select(e => new
            {
                e.ArtifactId,
                text = e.Text,
                e.SourceRefs,
                e.RelevanceScore,
                e.Mandatory,
                e.IsContradictionPair,
            }),
            charCount = sb.Length,
        });

        return new AssembledContext(state, manifest, excerpts.Where(e => included.Contains(e.ArtifactId)).ToList());
    }
}
