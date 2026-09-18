using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Capabilities;
using Relay.Core.Generation;
using Relay.Core.Ids;
using Relay.Core.Memory;
using Relay.Core.Storage;

namespace Relay.Core.Usage;

public sealed class ImprovementEvalResult
{
    public required bool Passed { get; init; }
    public required string FixtureName { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>Executes glossary/preference fixtures — does not merely check that descriptions exist.</summary>
public sealed class ImprovementEvaluator
{
    public IReadOnlyList<ImprovementEvalResult> EvaluateGlossaryProposal(
        GlossaryStore store,
        string projectId,
        string projectRoot,
        string otherProjectRoot,
        string acronym,
        string expectedExpansion)
    {
        var results = new List<ImprovementEvalResult>();
        var fake = new ScriptedTextGenerator(); // unused; zero-Jev path
        _ = fake;

        // Motivating: project resolve with proposed snapshot
        var capability = new AcronymResolveCapability(
            store,
            id => id == projectId ? projectRoot : otherProjectRoot,
            client: null);

        var motivate = capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "eval-motivate",
            Origin = "direct",
            ProjectId = projectId,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = acronym,
                ["span"] = $"What is {acronym}?",
            },
            At = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();

        results.Add(new ImprovementEvalResult
        {
            FixtureName = "motivating_project_resolve",
            Passed = motivate.Kind == AcronymResolveKinds.Resolved
                     && motivate.FeedText.Contains(expectedExpansion, StringComparison.Ordinal)
                     && string.Equals(motivate.Reason, "deterministic_glossary", StringComparison.Ordinal),
            Detail = $"{motivate.Kind}:{motivate.Reason}:{motivate.FeedText}",
        });

        // Counterexample: other project must not inherit
        var other = capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = AcronymResolveCapability.Id,
            CapabilityVersion = 1,
            CaseId = "eval-other",
            Origin = "direct",
            ProjectId = "other-project",
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acronym"] = acronym,
                ["span"] = $"What is {acronym}?",
            },
            At = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();

        results.Add(new ImprovementEvalResult
        {
            FixtureName = "other_project_no_leak",
            Passed = other.Kind != AcronymResolveKinds.Resolved
                     || !other.FeedText.Contains(expectedExpansion, StringComparison.Ordinal),
            Detail = $"{other.Kind}:{other.FeedText}",
        });

        // Counterexample: no_match when no candidates for empty glossary project
        results.Add(new ImprovementEvalResult
        {
            FixtureName = "ambiguous_or_unresolved_without_entry",
            Passed = other.Kind is AcronymResolveKinds.Unresolved or AcronymResolveKinds.Waiting
                     || other.Reason == "no_candidates",
            Detail = other.Reason ?? other.Kind,
        });

        return results;
    }
}

/// <summary>Project glossary improvement lifecycle: draft → evaluate → approve → shadow → active → revert.</summary>
public sealed class GlossaryImprovementLifecycle
{
    private readonly DataRoot _root;
    private readonly FrictionEvidenceStore _friction;
    private readonly GlossaryStore _glossary;

    public GlossaryImprovementLifecycle(DataRoot root)
    {
        _root = root;
        _friction = new FrictionEvidenceStore(root);
        _glossary = new GlossaryStore(root);
    }

    public FrictionEvidenceStore Friction => _friction;
    public GlossaryStore Glossary => _glossary;

    public string LifecycleDirectory => Path.Combine(_root.UsageDirectory, "improvement-lifecycle");

    public ImprovementProposal DraftFromSignature(
        PatternSignature signature,
        string expansion,
        DateTimeOffset now,
        int corrections = 3)
    {
        for (var i = 0; i < corrections; i++)
        {
            _friction.Capture(
                FrictionKinds.RepeatedCorrection,
                now.AddMinutes(i),
                signature.Key,
                expansion,
                caseId: $"case-{i}",
                exampleRefs: [$"corr:{signature.Subject}:{i}"],
                signature: signature);
        }

        var proposal = _friction.Suggest(now);
        proposal.Status = ImprovementStatuses.Draft;
        _friction.SaveProposal(proposal);
        SaveLifecycle(proposal.ProposalId, new LifecycleRecord
        {
            ProposalId = proposal.ProposalId,
            Status = ImprovementStatuses.Draft,
            ProjectId = signature.ProjectId,
            Acronym = signature.Subject,
            Expansion = expansion,
            ChangeSetHash = null,
        });
        return proposal;
    }

    public (bool Passed, ImprovementProposal Proposal, IReadOnlyList<ImprovementEvalResult> Results) Evaluate(
        string proposalId,
        string projectRoot,
        string otherProjectRoot)
    {
        var proposal = _friction.LoadProposal(proposalId)
            ?? throw new InvalidOperationException("proposal missing");
        var life = LoadLifecycle(proposalId)
            ?? throw new InvalidOperationException("lifecycle missing");

        proposal.Status = ImprovementStatuses.Evaluating;
        _friction.SaveProposal(proposal);

        // Evaluate against an isolated snapshot — do not mutate the live project glossary yet.
        var evalRoot = Path.Combine(_root.UsageDirectory, "eval-snapshots", proposalId);
        Directory.CreateDirectory(evalRoot);
        Directory.CreateDirectory(Path.Combine(evalRoot, ".orchestrator"));
        var entry = new GlossaryEntry
        {
            Id = "eval-" + life.Acronym.ToLowerInvariant(),
            Acronym = life.Acronym,
            Expansion = life.Expansion,
            Scope = GlossaryScopes.Project,
            ProjectId = life.ProjectId,
        };
        _glossary.SaveProject(evalRoot, [entry]);

        var otherEval = Path.Combine(evalRoot, "other");
        Directory.CreateDirectory(otherEval);
        Directory.CreateDirectory(Path.Combine(otherEval, ".orchestrator"));

        var results = new ImprovementEvaluator().EvaluateGlossaryProposal(
            _glossary,
            life.ProjectId,
            evalRoot,
            string.IsNullOrWhiteSpace(otherProjectRoot) ? otherEval : otherProjectRoot,
            life.Acronym,
            life.Expansion);

        var passed = results.All(r => r.Passed);
        proposal.Status = passed ? ImprovementStatuses.ReadyForApproval : ImprovementStatuses.EvaluationFailed;
        _friction.SaveProposal(proposal);
        life.Status = proposal.Status;
        life.EvalArtifactHash = HashJson(results);
        SaveLifecycle(proposalId, life);
        _ = projectRoot;
        return (passed, proposal, results);
    }

    public string Approve(string proposalId, string expectedEvalHash, string projectRoot, DateTimeOffset now)
    {
        var life = LoadLifecycle(proposalId) ?? throw new InvalidOperationException("lifecycle missing");
        var proposal = _friction.LoadProposal(proposalId) ?? throw new InvalidOperationException("proposal missing");
        if (proposal.Status != ImprovementStatuses.ReadyForApproval)
            throw new InvalidOperationException($"Cannot approve from status '{proposal.Status}'.");
        if (!string.Equals(life.EvalArtifactHash, expectedEvalHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Evaluation artifact hash mismatch.");

        var path = GlossaryStore.ProjectGlossaryPath(projectRoot);
        var before = File.Exists(path) ? File.ReadAllText(path) : null;
        var entry = new GlossaryEntry
        {
            Id = "bess-" + life.ProjectId[..Math.Min(8, life.ProjectId.Length)],
            Acronym = life.Acronym,
            Expansion = life.Expansion,
            Scope = GlossaryScopes.Project,
            ProjectId = life.ProjectId,
            SourceRefs = proposal.EvidenceIds.ToList(),
        };
        var afterList = new List<GlossaryEntry> { entry };
        var after = JsonSerializer.Serialize(afterList, RelayJson.Indented);
        var changeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((before ?? "") + "\n" + after)));

        AtomicFile.WriteAllText(path, after);
        life.BeforeImage = before;
        life.AfterImage = after;
        life.ChangeSetHash = changeHash;
        life.Status = ImprovementStatuses.ActiveShadow;
        life.ShadowRemaining = 3;
        proposal.Status = ImprovementStatuses.ActiveShadow;
        _friction.SaveProposal(proposal);
        SaveLifecycle(proposalId, life);
        _ = now;
        return changeHash;
    }

    public void RecordShadowUse(string proposalId)
    {
        var life = LoadLifecycle(proposalId) ?? throw new InvalidOperationException("lifecycle missing");
        var proposal = _friction.LoadProposal(proposalId) ?? throw new InvalidOperationException("proposal missing");
        if (life.Status != ImprovementStatuses.ActiveShadow) return;
        life.ShadowRemaining = Math.Max(0, life.ShadowRemaining - 1);
        if (life.ShadowRemaining == 0)
        {
            life.Status = ImprovementStatuses.Active;
            proposal.Status = ImprovementStatuses.Active;
            _friction.SaveProposal(proposal);
        }
        SaveLifecycle(proposalId, life);
    }

    public void Revert(string proposalId, string projectRoot, DateTimeOffset now)
    {
        var life = LoadLifecycle(proposalId) ?? throw new InvalidOperationException("lifecycle missing");
        var proposal = _friction.LoadProposal(proposalId) ?? throw new InvalidOperationException("proposal missing");
        var path = GlossaryStore.ProjectGlossaryPath(projectRoot);
        if (life.BeforeImage is null)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            AtomicFile.WriteAllText(path, life.BeforeImage);
        }

        life.Status = ImprovementStatuses.Reverted;
        life.RevertedAt = now;
        proposal.Status = ImprovementStatuses.Reverted;
        _friction.SaveProposal(proposal);
        SaveLifecycle(proposalId, life);
    }

    public LifecycleRecord? LoadLifecycle(string proposalId)
    {
        var path = Path.Combine(LifecycleDirectory, proposalId + ".json");
        var text = AtomicFile.ReadAllTextIfExists(path);
        return text is null ? null : JsonSerializer.Deserialize<LifecycleRecord>(text, RelayJson.Indented);
    }

    private void SaveLifecycle(string proposalId, LifecycleRecord record)
    {
        Directory.CreateDirectory(LifecycleDirectory);
        AtomicFile.WriteAllText(
            Path.Combine(LifecycleDirectory, proposalId + ".json"),
            JsonSerializer.Serialize(record, RelayJson.Indented));
    }

    private static string HashJson<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, RelayJson.Compact))));
}

public sealed class LifecycleRecord
{
    [JsonPropertyName("proposalId")] public required string ProposalId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; set; }
    [JsonPropertyName("projectId")] public required string ProjectId { get; init; }
    [JsonPropertyName("acronym")] public required string Acronym { get; init; }
    [JsonPropertyName("expansion")] public required string Expansion { get; init; }
    [JsonPropertyName("changeSetHash")] public string? ChangeSetHash { get; set; }
    [JsonPropertyName("evalArtifactHash")] public string? EvalArtifactHash { get; set; }
    [JsonPropertyName("beforeImage")] public string? BeforeImage { get; set; }
    [JsonPropertyName("afterImage")] public string? AfterImage { get; set; }
    [JsonPropertyName("shadowRemaining")] public int ShadowRemaining { get; set; }
    [JsonPropertyName("revertedAt")] public DateTimeOffset? RevertedAt { get; set; }
}
