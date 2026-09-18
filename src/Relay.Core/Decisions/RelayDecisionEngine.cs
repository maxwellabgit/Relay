using Relay.Core.Cases;
using Relay.Core.Judgments;
using Relay.Core.Privacy;

namespace Relay.Core.Decisions;

/// <summary>
/// Deterministic decision engine: assemble context → optional Jev question set → DecisionPolicy.
/// Uses FakeJudgmentClient or TypeSafe via JudgmentLifecycle; never treats confidence as authorization.
/// </summary>
public sealed class RelayDecisionEngine : ICaseDecisionEngine
{
    private readonly QuestionSetRegistry _questionSets;
    private readonly DecisionPolicy _policy;
    private readonly ContextAssembler _assembler;
    private readonly JudgmentLifecycle? _lifecycle;
    private readonly IJudgmentClient? _client;
    private readonly string _model;

    public RelayDecisionEngine(
        QuestionSetRegistry? questionSets = null,
        DecisionPolicy? policy = null,
        ContextAssembler? assembler = null,
        JudgmentLifecycle? lifecycle = null,
        IJudgmentClient? client = null,
        string model = "jev-1.13.0")
    {
        _questionSets = questionSets ?? QuestionSets.CreateV1();
        _policy = policy ?? new DecisionPolicy();
        _assembler = assembler ?? new ContextAssembler();
        _lifecycle = lifecycle;
        _client = client;
        _model = model;
    }

    public async Task<CaseDecision> DecideAsync(CaseDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PendingOperations.Any(o =>
                o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing))
        {
            return new CaseDecision
            {
                Kind = CaseDecisionKinds.Wait,
                FeedText = "Waiting on pending operation.",
                Reason = "pending_operation",
            };
        }

        if (request.Origin == CaseOrigin.Observed)
            return await DecideObservedAsync(request, cancellationToken).ConfigureAwait(false);

        return await DecideDirectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CaseDecision> DecideObservedAsync(CaseDecisionRequest request, CancellationToken cancellationToken)
    {
        if (request.RecentSegments.Count == 0)
        {
            return new CaseDecision
            {
                Kind = CaseDecisionKinds.Wait,
                FeedText = "Listening.",
                Reason = "no_segments",
            };
        }

        var set = _questionSets.Get(QuestionSets.ConversationScreenId, QuestionSets.ConversationScreenVersion);
        var state = request.AssembledState ?? _assembler.AssembleScreenState(request);
        var questions = FilterScreenQuestions(set, request);
        var success = await JudgeAsync(
            set,
            questions,
            state,
            request,
            HostedPurposes.ConversationScreen,
            cancellationToken).ConfigureAwait(false);

        if (success is null)
        {
            return new CaseDecision
            {
                Kind = CaseDecisionKinds.Wait,
                FeedText = "Waiting for judgment.",
                Reason = "waiting_for_judgment",
            };
        }

        return _policy.ApplyConversationScreen(success, request);
    }

    private async Task<CaseDecision> DecideDirectAsync(CaseDecisionRequest request, CancellationToken cancellationToken)
    {
        var set = _questionSets.Get(QuestionSets.DirectRouteId, QuestionSets.DirectRouteVersion);
        var state = request.AssembledState ?? _assembler.AssembleDirectState(request);
        var success = await JudgeAsync(
            set,
            set.Questions,
            state,
            request,
            HostedPurposes.DirectRouting,
            cancellationToken).ConfigureAwait(false);

        if (success is null)
        {
            return new CaseDecision
            {
                Kind = CaseDecisionKinds.PublishFeed,
                FeedText = "Hosted routing is unavailable; try again when judgments are allowed.",
                PresentationLevel = "persistent",
                Reason = "waiting_for_judgment",
            };
        }

        return _policy.ApplyDirectRoute(success, request);
    }

    private static IReadOnlyDictionary<string, JudgmentQuestion> FilterScreenQuestions(
        QuestionSetDefinition set,
        CaseDecisionRequest request)
    {
        // Include unresolved-term Noul only when a deterministic acronym-like candidate exists.
        var hasTermCandidate = request.RecentSegments.Any(s =>
            System.Text.RegularExpressions.Regex.IsMatch(s.Text, @"\b[A-Z]{2,6}\b"));
        if (hasTermCandidate)
            return set.Questions;

        return set.Questions
            .Where(kv => kv.Key != "contains_unresolved_term_request")
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private async Task<JudgmentSuccess?> JudgeAsync(
        QuestionSetDefinition set,
        IReadOnlyDictionary<string, JudgmentQuestion> questions,
        System.Text.Json.JsonElement state,
        CaseDecisionRequest request,
        string purpose,
        CancellationToken cancellationToken)
    {
        var judgmentRequest = new JudgmentRequest
        {
            QuestionSetId = set.Id,
            QuestionSetVersion = set.Version,
            Model = _model,
            State = state,
            Questions = questions,
            CaseId = request.CaseId,
            CaseVersion = request.Version,
            SourceObjectRefs = request.RecentSegments
                .Select(s => new JudgmentSourceRef(s.ObjectId, s.Sha256, SourceClassification.HostedAllowedSession))
                .ToList(),
        };

        if (_lifecycle is not null)
        {
            var result = await _lifecycle.ExecuteAsync(
                judgmentRequest,
                cancellationToken,
                appendCaseEvent: true,
                purpose: purpose,
                sessionId: request.SessionId,
                projectId: request.ProjectId).ConfigureAwait(false);
            return result.Response.Ok ? result.Response.Success : null;
        }

        if (_client is null)
            return null;

        var response = await _client.JudgeAsync(judgmentRequest, cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return null;
        response.ValidateAgainst(judgmentRequest);
        return response.Success;
    }
}
