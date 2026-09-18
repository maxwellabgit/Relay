using Relay.Core.Decisions;
using Relay.Core.Judgments;
using Relay.Core.Memory;
using Relay.Core.Privacy;

namespace Relay.Core.Capabilities;

public static class AcronymResolveKinds
{
    public const string Resolved = "resolved";
    public const string Unresolved = "unresolved";
    public const string Waiting = "waiting";
}

/// <summary>`glossary.acronym.resolve@1` — exact project hit is deterministic; else Jev Choice.</summary>
public sealed class AcronymResolveCapability : ICapabilityHandler
{
    public const string Id = "glossary.acronym.resolve";
    public const int CapabilityVersion = 1;
    public static string AtVersion => $"{Id}@{CapabilityVersion}";

    private readonly GlossaryStore _glossary;
    private readonly Func<string, string?> _projectRootResolver;
    private readonly JudgmentLifecycle? _lifecycle;
    private readonly IJudgmentClient? _client;
    private readonly DecisionPolicy _policy;
    private readonly ContextAssembler _assembler;
    private readonly string _model;

    public AcronymResolveCapability(
        GlossaryStore glossary,
        Func<string, string?> projectRootResolver,
        JudgmentLifecycle? lifecycle = null,
        IJudgmentClient? client = null,
        DecisionPolicy? policy = null,
        ContextAssembler? assembler = null,
        string model = "jev-1.13.0")
    {
        _glossary = glossary;
        _projectRootResolver = projectRootResolver;
        _lifecycle = lifecycle;
        _client = client;
        _policy = policy ?? new DecisionPolicy();
        _assembler = assembler ?? new ContextAssembler();
        _model = model;
    }

    public string CapabilityId => Id;
    public int Version => CapabilityVersion;

    public static CapabilityDefinition Definition { get; } = new()
    {
        Id = Id,
        Version = CapabilityVersion,
        AllowedOrigins = [CaseOriginObserved, CaseOriginDirect],
        SideEffectClass = CapabilitySideEffects.None,
        EvaluationFixtures =
        [
            "Acronym_exact_project_BESS_resolves_with_zero_Jev_calls",
            "Acronym_project_and_global_conflict_Jev_selects_project_context_match",
            "Acronym_no_match_publishes_unresolved_feed_without_invented_expansion",
        ],
        HandlerKey = Id,
    };

    // Avoid Cases dependency for constants used only as strings in AllowedOrigins.
    private const string CaseOriginObserved = "observed";
    private const string CaseOriginDirect = "direct";

    public async Task<CapabilityResult> HandleAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var span = request.Arguments.TryGetValue("span", out var s) ? s : request.Objective ?? "";
        var preferred = request.Arguments.TryGetValue("acronym", out var a) ? a : null;
        var acronym = AcronymCandidateBuilder.PrimaryToken(span, preferred);
        if (string.IsNullOrWhiteSpace(acronym))
        {
            return Unresolved("", "no_candidates");
        }

        var projectRoot = request.ProjectId is null ? null : _projectRootResolver(request.ProjectId);
        var candidates = AcronymCandidateBuilder.Build(acronym, _glossary, request.ProjectId, projectRoot);

        var projectHits = candidates.Where(c => c.Scope == GlossaryScopes.Project).ToList();
        var globalHits = candidates.Where(c => c.Scope == GlossaryScopes.Global).ToList();

        // Exactly one project-scoped entry and no competing distinct expansions → zero Jev.
        if (projectHits.Count == 1 &&
            globalHits.All(g => string.Equals(g.Expansion, projectHits[0].Expansion, StringComparison.OrdinalIgnoreCase)))
        {
            return Resolved(projectHits[0]);
        }

        if (candidates.Count == 0)
            return Unresolved(acronym, "no_candidates");

        // Single non-conflicting candidate (e.g. global-only exact).
        if (candidates.Count == 1)
            return Resolved(candidates[0]);

        // Ambiguous / project-vs-global conflict: Jev Choice
        var criteria = candidates.ToDictionary(c => c.CandidateId, c => c.Expansion, StringComparer.Ordinal);
        var set = QuestionSets.BuildAcronymSelect(criteria);
        var state = _assembler.AssembleAcronymState(acronym, span, request.ProjectId, candidates);
        var success = await JudgeAsync(set, state, request, cancellationToken).ConfigureAwait(false);
        if (success is null)
        {
            return new CapabilityResult
            {
                Kind = AcronymResolveKinds.Waiting,
                FeedText = "Waiting for judgment.",
                Reason = "waiting_for_judgment",
                Done = false,
            };
        }

        var decision = _policy.ApplyAcronymSelect(success, acronym, candidates);
        var unresolved = decision.Reason is "no_match" or "low_confidence" or "invalid_answer" or "unknown_choice";
        return new CapabilityResult
        {
            Kind = unresolved ? AcronymResolveKinds.Unresolved : AcronymResolveKinds.Resolved,
            FeedText = decision.FeedText,
            PresentationLevel = decision.PresentationLevel ?? "persistent",
            Done = true,
            Reason = decision.Reason,
            JudgmentIds = decision.JudgmentIds,
            SourceRefs = decision.SourceRefs,
            Artifacts = decision.Arguments.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? kv.Value.GetString() ?? ""
                    : kv.Value.GetRawText(),
                StringComparer.Ordinal),
        };
    }

    private static CapabilityResult Resolved(AcronymCandidate candidate) => new()
    {
        Kind = AcronymResolveKinds.Resolved,
        FeedText = $"{candidate.Acronym} — {candidate.Expansion}",
        PresentationLevel = "persistent",
        Done = true,
        Reason = "deterministic_glossary",
        Artifacts =
        {
            ["candidateId"] = candidate.CandidateId,
            ["expansion"] = candidate.Expansion,
            ["scope"] = candidate.Scope,
        },
        SourceRefs = candidate.EntryId is null ? [] : [candidate.CandidateId],
    };

    private static CapabilityResult Unresolved(string acronym, string reason) => new()
    {
        Kind = AcronymResolveKinds.Unresolved,
        FeedText = string.IsNullOrEmpty(acronym) ? "Unresolved: unknown term" : $"Unresolved: {acronym}",
        PresentationLevel = "persistent",
        Done = true,
        Reason = reason,
    };

    private async Task<JudgmentSuccess?> JudgeAsync(
        QuestionSetDefinition set,
        System.Text.Json.JsonElement state,
        CapabilityRequest request,
        CancellationToken cancellationToken)
    {
        var judgmentRequest = new JudgmentRequest
        {
            QuestionSetId = set.Id,
            QuestionSetVersion = set.Version,
            Model = _model,
            State = state,
            Questions = set.Questions,
            CaseId = request.CaseId,
            SourceObjectRefs = [],
        };

        if (_lifecycle is not null)
        {
            var result = await _lifecycle.ExecuteAsync(
                judgmentRequest,
                cancellationToken,
                appendCaseEvent: true,
                purpose: HostedPurposes.AcronymDisambiguation,
                sessionId: null,
                projectId: request.ProjectId).ConfigureAwait(false);
            return result.Response.Ok ? result.Response.Success : null;
        }

        if (_client is null) return null;
        var response = await _client.JudgeAsync(judgmentRequest, cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return null;
        response.ValidateAgainst(judgmentRequest);
        return response.Success;
    }
}
