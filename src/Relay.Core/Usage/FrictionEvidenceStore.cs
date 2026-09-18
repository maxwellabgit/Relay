using System.Globalization;
using System.Text;
using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Storage;

namespace Relay.Core.Usage;

/// <summary>
/// Append-only friction evidence under <c>usage/friction/</c>. Thresholds turn repeated
/// signals into typed <see cref="ImprovementProposal"/> drafts (never auto-applied).
/// </summary>
public sealed class FrictionEvidenceStore
{
    public const int DefaultThreshold = 3;

    private readonly DataRoot _root;
    private readonly object _gate = new();

    public FrictionEvidenceStore(DataRoot root) => _root = root;

    public string Directory => Path.Combine(_root.UsageDirectory, "friction");
    public string PathFor(DateTimeOffset at)
        => Path.Combine(Directory, at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    public string ProposalsDirectory => Path.Combine(_root.UsageDirectory, "improvements");

    public string? Record(FrictionEvidence evidence)
    {
        if (!FrictionKinds.All.Contains(evidence.Kind, StringComparer.Ordinal))
            return null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(evidence.At);
            var json = JsonSerializer.Serialize(evidence, RelayJson.Compact) + "\n";
            lock (_gate)
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public FrictionEvidence Capture(
        string kind,
        DateTimeOffset at,
        string pattern,
        string detail,
        string? caseId = null,
        IEnumerable<string>? exampleRefs = null,
        int count = 1,
        PatternSignature? signature = null)
    {
        var evidence = new FrictionEvidence
        {
            EvidenceId = Ulid.NewUlid(at),
            Kind = kind,
            At = at,
            CaseId = caseId,
            Pattern = pattern,
            Detail = detail,
            ExampleRefs = exampleRefs?.ToList() ?? [],
            Count = count,
            SignatureKey = signature?.Key,
            ProjectId = signature?.ProjectId,
            Subject = signature?.Subject,
            CapabilityId = signature?.CapabilityId,
            Category = signature?.Category,
        };
        Record(evidence);
        return evidence;
    }

    public IReadOnlyList<FrictionEvidence> ReadRecent(DateTimeOffset day, int lookbackDays = 7)
    {
        var list = new List<FrictionEvidence>();
        for (var i = 0; i < lookbackDays; i++)
        {
            var path = PathFor(day.AddDays(-i));
            if (!File.Exists(path)) continue;
            foreach (var raw in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<FrictionEvidence>(raw, RelayJson.Compact);
                    if (e is not null) list.Add(e);
                }
                catch (JsonException) { /* skip torn line */ }
            }
        }
        return list.OrderBy(e => e.At).ToList();
    }

    /// <summary>
    /// Builds a typed improvement proposal when one <see cref="PatternSignature"/> reaches threshold.
    /// Groups by complete signature — not friction kind alone.
    /// </summary>
    public ImprovementProposal Suggest(DateTimeOffset now, int threshold = DefaultThreshold)
    {
        var recent = ReadRecent(now);
        var grouped = recent
            .GroupBy(e => e.SignatureKey ?? $"legacy|{e.Kind}|{e.Pattern}", StringComparer.Ordinal)
            .Select(g => (
                Signature: g.Key,
                Kind: g.First().Kind,
                Items: g.ToList(),
                Score: g.Sum(x => Math.Max(1, x.Count)),
                ProjectId: g.First().ProjectId,
                Subject: g.First().Subject,
                CapabilityId: g.First().CapabilityId,
                Category: g.First().Category,
                Pattern: g.First().Pattern))
            .Where(g => g.Score >= threshold)
            .OrderByDescending(g => g.Score)
            .ToList();

        if (grouped.Count == 0)
        {
            return new ImprovementProposal
            {
                ProposalId = Ulid.NewUlid(now),
                Kind = ImprovementKinds.NoChange,
                Title = "No repeated friction above threshold",
                ExpectedBenefit = "None — wait for more evidence.",
                ActivationScope = "none",
                ReversionPlan = "n/a",
                SuccessMetric = "n/a",
                DraftedAt = now,
                Status = ImprovementStatuses.Draft,
            };
        }

        var top = grouped[0];
        var examples = top.Items.SelectMany(i => i.ExampleRefs.DefaultIfEmpty(i.EvidenceId)).Distinct().Take(8).ToList();
        var evidenceIds = top.Items.Select(i => i.EvidenceId).ToList();
        var proposal = BuildForKind(
            top.Kind,
            examples,
            evidenceIds,
            top.Pattern,
            now,
            top.ProjectId,
            top.Subject,
            top.CapabilityId);
        if (proposal.Kind is ImprovementKinds.Workflow or ImprovementKinds.Tool or ImprovementKinds.HostCapability)
            proposal.Status = ImprovementStatuses.UnsupportedForActivation;
        SaveProposal(proposal);
        return proposal;
    }

    public string SaveProposal(ImprovementProposal proposal)
    {
        System.IO.Directory.CreateDirectory(ProposalsDirectory);
        var path = Path.Combine(ProposalsDirectory, proposal.ProposalId + ".json");
        AtomicFile.WriteAllText(path, proposal.ToJson());
        return path;
    }

    public ImprovementProposal? LoadProposal(string proposalId)
    {
        var path = Path.Combine(ProposalsDirectory, proposalId + ".json");
        var text = AtomicFile.ReadAllTextIfExists(path);
        return text is null ? null : ImprovementProposal.FromJson(text);
    }

    private static ImprovementProposal BuildForKind(
        string frictionKind,
        IReadOnlyList<string> examples,
        IReadOnlyList<string> evidenceIds,
        string pattern,
        DateTimeOffset now,
        string? projectId = null,
        string? subject = null,
        string? capabilityId = null)
    {
        return frictionKind switch
        {
            FrictionKinds.RepeatedCorrection => new ImprovementProposal
            {
                ProposalId = Ulid.NewUlid(now),
                Kind = ImprovementKinds.Memory,
                Title = string.IsNullOrWhiteSpace(subject)
                    ? "Record a durable correction memory"
                    : $"Project glossary: {subject}",
                Examples = examples,
                ExpectedBenefit = "Stop re-asking the user to correct the same fact.",
                Inputs = ["corrected_fact", "project_or_topic", subject ?? "", projectId ?? ""],
                Outputs = ["filed_note_or_memory_ref", "glossary_entry"],
                Permissions = ["note.write", "memory.write"],
                EvaluationCases =
                [
                    new("same_topic_recall", "Ask the corrected fact again", "Answer cites the stored correction without re-prompting"),
                    new("unrelated_topic", "Ask a different topic", "Does not invent the correction for unrelated asks"),
                    new("other_project", "Same acronym in another project", "Does not inherit this project's expansion"),
                ],
                ActivationScope = string.IsNullOrWhiteSpace(projectId)
                    ? "project-or-global memory for this fact"
                    : $"project:{projectId}",
                ReversionPlan = "Supersede or archive the memory note; prior absence restored.",
                SuccessMetric = "Zero repeated corrections on the same fact in the next 5 related asks.",
                FrictionKind = frictionKind,
                EvidenceIds = evidenceIds,
                DraftedAt = now,
                Status = ImprovementStatuses.Draft,
            },
            FrictionKinds.RepeatedToolSequence => new ImprovementProposal
            {
                ProposalId = Ulid.NewUlid(now),
                Kind = ImprovementKinds.Workflow,
                Title = "Promote a repeated tool sequence to a workflow",
                Examples = examples,
                ExpectedBenefit = "One approved workflow replaces the repeated multi-step path.",
                Inputs = ["workflow_inputs_from_examples"],
                Outputs = ["workflow_result"],
                Permissions = ["workflow.promote", "tool:union_of_steps"],
                EvaluationCases =
                [
                    new("fixture_pass", "Run fixtures matching the repeated sequence", "All fixtures pass before promote"),
                    new("reuse", "Trigger the same ask again", "run_workflow is used; no rebuild"),
                ],
                ActivationScope = "promoted workflow available to the mind",
                ReversionPlan = "Revert the workflow change set; definition removed.",
                SuccessMetric = "Next similar ask uses the workflow in one step.",
                FrictionKind = frictionKind,
                EvidenceIds = evidenceIds,
                DraftedAt = now,
                Status = ImprovementStatuses.UnsupportedForActivation,
            },
            FrictionKinds.FailedCapability => new ImprovementProposal
            {
                ProposalId = Ulid.NewUlid(now),
                Kind = ImprovementKinds.Tool,
                Title = "Build a tool for a repeated capability gap",
                Examples = examples,
                ExpectedBenefit = "Close the gap with a tested, reversible tool instead of failing.",
                Inputs = ["generalized_variable_inputs"],
                Outputs = ["tool_result_keys"],
                Permissions = ["tool.promote", "host:declared_catalog_only"],
                EvaluationCases =
                [
                    new("counterexample_tests", "Tests use inputs not from the original ask", "Sandbox tests pass"),
                    new("reuse", "Later ask in another city/input", "use_tool without rebuild"),
                ],
                ActivationScope = "promoted tool in tools/",
                ReversionPlan = "Revert tool change set; file removed.",
                SuccessMetric = "Capability succeeds on the next similar ask without a new build.",
                FrictionKind = frictionKind,
                EvidenceIds = evidenceIds,
                DraftedAt = now,
                Status = ImprovementStatuses.UnsupportedForActivation,
            },
            FrictionKinds.RepeatedFiling => new ImprovementProposal
            {
                ProposalId = Ulid.NewUlid(now),
                Kind = ImprovementKinds.Preference,
                Title = "Standing preference for repeated filing shape",
                Examples = examples,
                ExpectedBenefit = "Fewer prompts for the same filing destination/type.",
                Inputs = ["preference_key", "preference_value"],
                Outputs = ["updated_preferences.json"],
                Permissions = ["preference.update"],
                EvaluationCases =
                [
                    new("same_shape", "File another note of the same shape", "Uses standing preference without re-asking"),
                ],
                ActivationScope = "preferences.json key",
                ReversionPlan = "Revert preference change set to before image.",
                SuccessMetric = "Filing of this shape needs no extra confirmation in the next 3 cases.",
                FrictionKind = frictionKind,
                EvidenceIds = evidenceIds,
                DraftedAt = now,
                Status = ImprovementStatuses.Draft,
            },
            FrictionKinds.ExcessiveIntervention => new ImprovementProposal
            {
                ProposalId = Ulid.NewUlid(now),
                Kind = ImprovementKinds.HostCapability,
                Title = "Reduce excessive intervention via host grant or quieter policy",
                Examples = examples,
                ExpectedBenefit = "Fewer unnecessary approval cards for low-risk repeated actions.",
                Inputs = ["intervention_pattern", pattern],
                Outputs = ["narrower_approval_scope_or_standing_grant"],
                Permissions = ["host_capability.propose", "policy.scope"],
                EvaluationCases =
                [
                    new("same_action", "Repeat the low-risk action", "No extra approval when standing grant covers it"),
                    new("higher_risk", "Attempt a higher-risk action", "Still requires approval"),
                ],
                ActivationScope = "standing grant or policy fragment; never the permission broker itself",
                ReversionPlan = "Revoke standing grant / revert policy fragment change set.",
                SuccessMetric = "Intervention count for this pattern drops by ≥50% over the next day of use.",
                FrictionKind = frictionKind,
                EvidenceIds = evidenceIds,
                DraftedAt = now,
                Status = ImprovementStatuses.UnsupportedForActivation,
            },
            _ => new ImprovementProposal
            {
                ProposalId = Ulid.NewUlid(now),
                Kind = ImprovementKinds.NoChange,
                Title = "Unrecognized friction kind",
                ExpectedBenefit = "None",
                ActivationScope = "none",
                ReversionPlan = "n/a",
                SuccessMetric = "n/a",
                FrictionKind = frictionKind,
                EvidenceIds = evidenceIds,
                DraftedAt = now,
                Status = ImprovementStatuses.Draft,
            },
        };
    }
}
