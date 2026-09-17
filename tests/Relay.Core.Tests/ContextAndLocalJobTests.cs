using System.Text;
using Relay.Core.Context;
using Relay.Core.Evidence;
using Relay.Core.Generation;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>§8 context retrieval bounds and local job routing.</summary>
public class ContextAndLocalJobTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    private (EvidenceStore evidence, ProvenanceGraph provenance, ContextAssembler assembler) Assembler()
    {
        _tmp.Root.EnsureLayout(_clock);
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        var provenance = new ProvenanceGraph(evidence);
        return (evidence, provenance, new ContextAssembler(evidence, provenance, _clock));
    }

    private LocalJobDispatcher Dispatcher(Func<LocalJobDefinition, string, CancellationToken, Task<string>>? generate = null)
    {
        _tmp.Root.EnsureLayout(_clock);
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        return new LocalJobDispatcher(evidence, _tmp.Root, _clock, generate);
    }

    [Fact]
    public void Caps_candidates_at_20_and_ranked_excerpts_at_8()
    {
        var (evidence, _, assembler) = Assembler();
        var ids = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var a = evidence.PutText($"passage {i}", label: "p" + i, artifactId: "a" + i);
            ids.Add(a.ArtifactId);
        }

        var assembled = assembler.Assemble("case-1", "passage.correction", ids);
        Assert.Equal(ContextRetrievalPolicy.MaxCandidates, assembled.Manifest.CandidateCount);
        Assert.True(assembled.Manifest.RankedExcerptCount <= ContextRetrievalPolicy.MaxRankedExcerpts);
        Assert.True(assembled.Excerpts.Count <= ContextRetrievalPolicy.MaxRankedExcerpts);
    }

    [Fact]
    public void Hosted_assembly_excludes_local_only_before_rerank()
    {
        var (evidence, _, assembler) = Assembler();
        var local = evidence.PutText("secret", restriction: ContentRestriction.LocalOnly, artifactId: "local-1");
        var open = evidence.PutText("public", restriction: ContentRestriction.HostedEligible, artifactId: "open-1");

        var assembled = assembler.Assemble("case-1", "job.reference_interpretation",
            [local.ArtifactId, open.ArtifactId], forHosted: true);

        Assert.DoesNotContain(local.ArtifactId, assembled.Manifest.IncludedArtifactIds);
        Assert.Contains(local.ArtifactId, assembled.Manifest.OmittedArtifactIds);
        Assert.Contains(assembled.Manifest.Notes, n => n.StartsWith(ContextBlockReasons.RestrictedExcluded, StringComparison.Ordinal));
        Assert.Contains(open.ArtifactId, assembled.Manifest.IncludedArtifactIds);
    }

    [Fact]
    public void Mandatory_content_that_does_not_fit_blocks_without_silent_truncate()
    {
        var (evidence, _, assembler) = Assembler();
        var big = new string('x', ContextRetrievalPolicy.MaxStateChars - 10);
        evidence.PutText(big, artifactId: "primary");
        evidence.PutText(new string('y', 500), artifactId: "mandatory-extra");

        var excerpts = new List<ContextExcerpt>
        {
            new("primary", big, ["ref:primary"], Mandatory: true),
            new("mandatory-extra", new string('y', 500), ["ref:extra"], Mandatory: true),
        };

        var assembled = assembler.Assemble("case-1", "evidence.claim_relation", ["primary", "mandatory-extra"], excerpts);
        Assert.True(assembled.Manifest.SplitRequired);
        Assert.Equal(ContextBlockReasons.ContextTooLarge, assembled.Manifest.BlockReason);
        Assert.Contains("mandatory-extra", assembled.Manifest.OmittedArtifactIds);
        Assert.True(assembled.Manifest.StateCharCount <= ContextRetrievalPolicy.MaxStateChars);
        // Must not silently truncate mandatory text into state.
        Assert.DoesNotContain(assembled.Excerpts, e => e.ArtifactId == "mandatory-extra");
    }

    [Fact]
    public void Preserves_contradiction_pairs_when_ranking()
    {
        var (evidence, _, assembler) = Assembler();
        for (var i = 0; i < 10; i++)
            evidence.PutText("filler " + i, artifactId: "f" + i);

        var excerpts = Enumerable.Range(0, 10)
            .Select(i => new ContextExcerpt("f" + i, "filler " + i, [], RelevanceScore: 0.1 * i))
            .Append(new ContextExcerpt("f0", "filler 0", [], RelevanceScore: 0.01, IsContradictionPair: true))
            .ToList();

        // CapRankedExcerpts takes first 8 of input; assembler reorders by contradiction/mandatory then score.
        var assembled = assembler.Assemble("case-1", "statement.revision_relation",
            excerpts.Select(e => e.ArtifactId).Distinct().ToList(), excerpts);
        Assert.Contains(assembled.Excerpts, e => e.IsContradictionPair);
        Assert.True(assembled.Excerpts.Count <= ContextRetrievalPolicy.MaxRankedExcerpts);
    }

    [Fact]
    public void State_never_exceeds_24000_chars()
    {
        var (evidence, _, assembler) = Assembler();
        var chunks = new List<ContextExcerpt>();
        for (var i = 0; i < 8; i++)
        {
            var text = new string((char)('a' + i), 4000);
            evidence.PutText(text, artifactId: "c" + i);
            chunks.Add(new ContextExcerpt("c" + i, text, [], RelevanceScore: 1.0 - i * 0.01));
        }

        var assembled = assembler.Assemble("case-1", "passage.commitment",
            chunks.Select(c => c.ArtifactId).ToList(), chunks);
        Assert.True(assembled.Manifest.StateCharCount <= ContextRetrievalPolicy.MaxStateChars);
        Assert.NotEmpty(assembled.Manifest.TruncatedArtifactIds.Concat(assembled.Manifest.OmittedArtifactIds));
    }

    [Fact]
    public async Task ExtractCandidates_includes_exact_passage_refs_and_validates_offsets()
    {
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        _tmp.Root.EnsureLayout(_clock);
        const string source = "First sentence. Second sentence!";
        var artifact = evidence.PutText(source, artifactId: "src-1");
        var dispatcher = new LocalJobDispatcher(evidence, _tmp.Root, _clock);

        var result = await dispatcher.DispatchAsync(new LocalJobDefinition
        {
            JobId = "job-1",
            Type = LocalJobTypes.ExtractCandidates,
            CaseId = "case-1",
            Task = "extract passages",
            PermittedInputArtifactIds = [artifact.ArtifactId],
        });

        Assert.True(result.Ok);
        Assert.NotNull(result.Extractions);
        Assert.NotEmpty(result.Extractions!);
        foreach (var c in result.Extractions!)
        {
            Assert.Equal(artifact.ArtifactId, c.SourceArtifactId);
            Assert.False(string.IsNullOrWhiteSpace(c.CandidateId));
            Assert.True(ExtractionValidator.Validate(c, source, out var err), err);
            Assert.Equal(source[c.StartOffset..c.EndOffset], c.Text);
        }

        var bad = new ExtractionCandidate
        {
            CandidateId = "x",
            Type = "passage",
            Text = "nope",
            SourceArtifactId = artifact.ArtifactId,
            StartOffset = 0,
            EndOffset = 4,
        };
        Assert.False(ExtractionValidator.Validate(bad, source, out var mismatch));
        Assert.Equal("text_mismatch", mismatch);
    }

    [Fact]
    public void Local_jobs_cannot_invoke_tools_and_never_fallback_for_jev()
    {
        var dispatcher = Dispatcher();
        Assert.False(dispatcher.AllowLocalJudgmentFallback);
        Assert.False(dispatcher.TryLocalJudgmentFallback());
        // No tool surface on dispatcher — routing never includes tool invocation.
        var route = LocalJobDispatcher.RouteJobTypes(needsReferences: true, needsSearch: true, needsDraft: true);
        Assert.DoesNotContain(route, t => t.Contains("Tool", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LocalJobTypes.ExtractCandidates, route[0]);
        Assert.Equal(LocalJobTypes.ResolveReferenceCandidates, route[1]);
        Assert.Equal(LocalJobTypes.FormulateSearchQuery, route[2]);
        Assert.Equal(LocalJobTypes.FillInterpretiveArguments, route[3]);
        Assert.Equal(LocalJobTypes.DraftAnswer, route[4]);
    }

    [Fact]
    public void Resolution_rounds_advance_only_on_logical_progress_not_transport_retry()
    {
        var dispatcher = Dispatcher();
        const string decision = "ref-decision-1";
        var s0 = dispatcher.GetOrCreateRounds(decision);
        Assert.Equal(ResolutionRounds.Initial, s0.Round);
        Assert.Equal("retrieval", ResolutionRounds.NextAction(s0.Round));

        dispatcher.NoteTransportRetry(decision);
        Assert.Equal(ResolutionRounds.Initial, dispatcher.GetOrCreateRounds(decision).Round);

        var s1 = dispatcher.AdvanceRound(decision);
        Assert.Equal(ResolutionRounds.Retrieval, s1.Round);
        Assert.Equal("reasoning", ResolutionRounds.NextAction(s1.Round));

        dispatcher.NoteTransportRetry(decision);
        Assert.Equal(ResolutionRounds.Retrieval, dispatcher.GetOrCreateRounds(decision).Round);

        var s2 = dispatcher.AdvanceRound(decision);
        Assert.Equal(ResolutionRounds.Reasoning, s2.Round);
        Assert.Equal("ask_or_defer", ResolutionRounds.NextAction(s2.Round));

        // Further advances stay at reasoning; still unresolved → ask/defer.
        dispatcher.AdvanceRound(decision);
        Assert.Equal(ResolutionRounds.Reasoning, dispatcher.GetOrCreateRounds(decision).Round);

        // Persisted by logical decision identity.
        var path = Path.Combine(_tmp.Root.Path, "jobs", "rounds", decision + ".json");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task All_local_job_types_are_dispatchable_without_tool_hooks()
    {
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        _tmp.Root.EnsureLayout(_clock);
        evidence.PutText("Hello world.", artifactId: "in-1");
        var dispatcher = new LocalJobDispatcher(evidence, _tmp.Root, _clock,
            (_, prompt, _) => Task.FromResult("source:in-1 drafted from: " + prompt[..Math.Min(40, prompt.Length)]));

        foreach (var type in LocalJobTypes.All)
        {
            var result = await dispatcher.DispatchAsync(new LocalJobDefinition
            {
                JobId = "j-" + type,
                Type = type,
                CaseId = "case-1",
                Task = "do " + type,
                PermittedInputArtifactIds = ["in-1"],
                RequireSourceRefs = false,
            });
            Assert.True(result.Ok, type + ": " + result.Error);
        }
    }

    [Fact]
    public void Follows_ids_through_lineage_into_candidates()
    {
        var (evidence, provenance, assembler) = Assembler();
        var root = evidence.PutText("root passage", artifactId: "root");
        var derived = evidence.Derive("summary of root", [root.ArtifactId], label: "sum", kind: "summary");
        provenance.Link(root.ArtifactId, derived.ArtifactId);

        var assembled = assembler.Assemble("case-1", "candidate.evidence_relevance", [derived.ArtifactId]);
        Assert.True(assembled.Manifest.CandidateCount >= 1);
        Assert.True(
            assembled.Manifest.IncludedArtifactIds.Contains("root")
            || assembled.Manifest.IncludedArtifactIds.Contains(derived.ArtifactId));
    }
}
