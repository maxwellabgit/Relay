using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core.Judgments;
using Relay.Core.Tests.Support;
using Relay.Core.Usage;
using Relay.Core.Processes;

namespace Relay.Core.Tests;

/// <summary>§10 production workflows: definitions, policies, research citations, improvement triggers.</summary>
public class WorkflowProductionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 22, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    private static string WorkflowsDir()
    {
        // Prefer repo workflows/v1 next to test output or walk up from BaseDirectory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "workflows", "v1");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "workflows", "v1"));
    }

    [Fact]
    public void Loads_and_validates_all_four_production_workflows()
    {
        var dir = WorkflowsDir();
        Assert.True(Directory.Exists(dir), "missing " + dir);
        var defs = ProcessCatalog.LoadDirectory(dir);
        Assert.Contains(defs, d => d.Id == ProductionWorkflows.RecallFact);
        Assert.Contains(defs, d => d.Id == ProductionWorkflows.CheckPlanImpact);
        Assert.Contains(defs, d => d.Id == ProductionWorkflows.ResearchClaim);
        Assert.Contains(defs, d => d.Id == ProductionWorkflows.PrepareImprovement);

        _tmp.Root.EnsureLayout(_clock);
        var engine = new ProcessEngine(_tmp.Root, _clock);
        foreach (var def in defs)
        {
            var v = engine.Validate(def);
            Assert.True(v.Ok, def.Id + ": " + string.Join(",", v.Errors));
            Assert.All(def.Nodes, n => Assert.Contains(n.Type, ProcessNodeTypes.All));
            Assert.All(def.Edges.Where(e => e.Retry is not null), e => Assert.True(e.Retry!.MaxAttempts >= 1));
        }
    }

    [Fact]
    public void Persists_node_position_and_outputs_across_advance()
    {
        _tmp.Root.EnsureLayout(_clock);
        var def = ProcessCatalog.Load(Path.Combine(WorkflowsDir(), "recall_fact.json"));
        var engine = new ProcessEngine(_tmp.Root, _clock);
        var run = engine.Start(def, "case-1");
        Assert.Equal("retrieve_candidates", run.CurrentNodeId);

        run = engine.Advance(def, run, output: JsonSerializer.SerializeToElement(new { found = 2 }));
        Assert.Equal("judge_claim_relations", run.CurrentNodeId);
        Assert.True(run.NodeOutputs.ContainsKey("retrieve_candidates"));

        var reloaded = engine.TryLoad(run.RunId)!;
        Assert.Equal(run.CurrentNodeId, reloaded.CurrentNodeId);
        Assert.True(reloaded.NodeOutputs.ContainsKey("retrieve_candidates"));
    }

    [Fact]
    public void Detects_cycles_and_unknown_node_types()
    {
        _tmp.Root.EnsureLayout(_clock);
        var engine = new ProcessEngine(_tmp.Root, _clock);
        var bad = new ProcessDefinition
        {
            Id = "bad",
            Entry = "a",
            Nodes =
            [
                new ProcessNode { Id = "a", Type = "retrieve" },
                new ProcessNode { Id = "b", Type = "not_a_type" },
            ],
            Edges =
            [
                new ProcessEdge { From = "a", To = "b" },
                new ProcessEdge { From = "b", To = "a" },
            ],
        };
        var v = engine.Validate(bad);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.StartsWith("unknown_node_type", StringComparison.Ordinal));
        Assert.Contains(v.Errors, e => e.StartsWith("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Newer_statement_is_not_auto_accepted_as_fact()
    {
        var older = ("s1", "Ship date is Oct 1", T0, (string?)null);
        var newer = ("s2", "Ship date is Nov 1", T0.AddDays(1), (string?)null);
        var outcome = RecallFactPolicy.Resolve([older, newer], revisionRelation: null, claimRelation: null);
        Assert.False(outcome.Answered);
        Assert.Equal("newer_not_auto_accepted", outcome.BlockReason);
        Assert.NotEmpty(outcome.ConflictIds);
    }

    [Fact]
    public void Authority_revision_allows_answer_with_provenance()
    {
        var older = ("s1", "Ship date is Oct 1", T0, (string?)null);
        var newer = ("s2", "Ship date is Nov 1", T0.AddDays(1), "user:alice");
        var revision = new DecisionInterpretation
        {
            DecisionId = "statement.revision_relation",
            QuestionKey = "revision_relation",
            Primitive = "choice",
            Polarity = DecisionPolarity.Positive,
            ChosenOption = "revision",
        };
        var outcome = RecallFactPolicy.Resolve([older, newer], revision, null);
        Assert.True(outcome.Answered);
        Assert.Equal("Ship date is Nov 1", outcome.Answer);
        Assert.Contains(outcome.ProvenanceRefs, r => r.Contains("authority:", StringComparison.Ordinal));
    }

    [Fact]
    public void Disputed_date_blocks_consequential_plan_ops()
    {
        Assert.True(PlanImpactPolicy.BlocksConsequentialOps(dateDisputed: true));
        var okCriterion = new DecisionInterpretation
        {
            DecisionId = "output.criterion_satisfied",
            QuestionKey = "criterion_satisfied",
            Primitive = "choice",
            Polarity = DecisionPolarity.Positive,
            ChosenOption = "satisfied",
        };
        Assert.Equal("disputed_date", PlanImpactPolicy.Evaluate(true, [okCriterion]));
        Assert.Null(PlanImpactPolicy.Evaluate(false, [okCriterion]));
    }

    [Fact]
    public void Research_requires_explicit_citations_and_valid_hashes()
    {
        const string source = "Lightshift operates 20 sites.";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        var excerpt = new ResearchClaimPolicy.ExcerptRef("a1", hash, source, 0, source.Length);
        Assert.True(ResearchClaimPolicy.ValidateExcerpt(excerpt, source, out _));

        var badHash = excerpt with { ContentHash = "deadbeef" };
        Assert.False(ResearchClaimPolicy.ValidateExcerpt(badHash, source, out var err));
        Assert.Equal("hash_mismatch", err);

        var support = new DecisionInterpretation
        {
            DecisionId = "output.claim_support",
            QuestionKey = "claim_support",
            Primitive = "choice",
            Polarity = DecisionPolarity.Positive,
            ChosenOption = "supported",
        };
        Assert.False(ResearchClaimPolicy.AllowClaim(support, []));
        Assert.True(ResearchClaimPolicy.AllowClaim(support, ["a1"]));

        var cites = ResearchClaimPolicy.CitationsOnly(["a1", "a2", "a3"], ["a1", "x"]);
        Assert.Equal(["a1"], cites);
    }

    [Fact]
    public void Exact_arithmetic_is_computed_in_code()
    {
        Assert.True(ExactArithmetic.TryAdd("20", "5", out var sum, out _));
        Assert.Equal(25m, sum);
        Assert.True(ExactArithmetic.TryMultiply("20", "1.5", out var prod, out _));
        Assert.Equal(30m, prod);
        Assert.False(ExactArithmetic.TryAdd("twenty", "5", out _, out var err));
        Assert.Equal("non_numeric", err);
    }

    [Fact]
    public void Improvement_triggers_on_threshold_across_sessions_or_explicit_request()
    {
        var evidence = new List<FrictionEvidence>
        {
            new() { EvidenceId = "e1", Kind = FrictionKinds.FailedCapability, At = T0, SessionId = "s1", Count = 1 },
            new() { EvidenceId = "e2", Kind = FrictionKinds.FailedCapability, At = T0, SessionId = "s1", Count = 1 },
            new() { EvidenceId = "e3", Kind = FrictionKinds.FailedCapability, At = T0, SessionId = "s2", Count = 1 },
        };
        Assert.True(ImprovementTriggerPolicy.ShouldPrepare(false, evidence, out var reason));
        Assert.Equal("threshold_met", reason);

        var tooFewSessions = evidence.Take(2).ToList();
        Assert.False(ImprovementTriggerPolicy.ShouldPrepare(false, tooFewSessions, out _));
        Assert.True(ImprovementTriggerPolicy.ShouldPrepare(true, [], out var explicitReason));
        Assert.Equal("explicit_request", explicitReason);
        Assert.False(ImprovementTriggerPolicy.MayBuild(hasGrant: false));
        Assert.True(ImprovementTriggerPolicy.MayBuild(hasGrant: true));
    }

    [Fact]
    public void Retry_edges_respect_explicit_bounds()
    {
        _tmp.Root.EnsureLayout(_clock);
        var engine = new ProcessEngine(_tmp.Root, _clock);
        var def = new ProcessDefinition
        {
            Id = "retry-demo",
            Entry = "a",
            Nodes =
            [
                new ProcessNode { Id = "a", Type = "tool" },
                new ProcessNode { Id = "b", Type = "complete" },
            ],
            Edges =
            [
                new ProcessEdge { From = "a", To = "b" },
                new ProcessEdge { From = "a", To = "a", Retry = new ProcessRetryBound { MaxAttempts = 2 } },
            ],
        };
        Assert.True(engine.Validate(def).Ok);
        var run = engine.Start(def);
        Assert.True(engine.TryRetry(def, run, "a"));
        Assert.True(engine.TryRetry(def, run, "a"));
        Assert.False(engine.TryRetry(def, run, "a"));
    }
}
