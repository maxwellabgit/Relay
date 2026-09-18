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
        var (success, failure) = await JudgeAsync(
            set,
            questions,
            state,
            request,
            HostedPurposes.ConversationScreen,
            cancellationToken).ConfigureAwait(false);

        if (success is null)
        {
            if (failure is { Retryable: true })
            {
                return new CaseDecision
                {
                    Kind = CaseDecisionKinds.Wait,
                    FeedText = "Waiting for judgment.",
                    Reason = "waiting_for_judgment",
                    Arguments = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
                    {
                        ["failureCategory"] = System.Text.Json.JsonSerializer.SerializeToElement(failure.Category),
                        ["retryable"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                    },
                };
            }

            return new CaseDecision
            {
                Kind = CaseDecisionKinds.Wait,
                FeedText = "Hosted judgment is not available for this input.",
                Reason = failure?.Category ?? "judgment_unavailable",
                Arguments = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
                {
                    ["retryable"] = System.Text.Json.JsonSerializer.SerializeToElement(false),
                },
            };
        }

        return _policy.ApplyConversationScreen(success, request);
    }

    private async Task<CaseDecision> DecideDirectAsync(CaseDecisionRequest request, CancellationToken cancellationToken)
    {
        var set = _questionSets.Get(QuestionSets.DirectRouteId, QuestionSets.DirectRouteVersion);
        var state = request.AssembledState ?? _assembler.AssembleDirectState(request);
        var (success, failure) = await JudgeAsync(
            set,
            set.Questions,
            state,
            request,
            HostedPurposes.DirectRouting,
            cancellationToken).ConfigureAwait(false);

        if (success is null)
        {
            if (failure is { Retryable: true })
            {
                return new CaseDecision
                {
                    Kind = CaseDecisionKinds.Wait,
                    FeedText = "Waiting for judgment.",
                    Reason = "waiting_for_judgment",
                };
            }

            return new CaseDecision
            {
                Kind = CaseDecisionKinds.PublishFeed,
                FeedText = "Hosted routing is unavailable; try again when judgments are allowed.",
                PresentationLevel = "persistent",
                Reason = failure?.Category ?? "waiting_for_judgment",
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

    private async Task<(JudgmentSuccess? Success, JudgmentFailure? Failure)> JudgeAsync(
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
            SourceObjectRefs = BuildSourceRefs(request),
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
            if (result.Response.Ok)
                return (result.Response.Success, null);
            return (null, result.Response.Failure);
        }

        // Tests only — production composition (RelayComposition / CaseRelayHost) must pass lifecycle, not a raw client.
        if (_client is null)
            return (null, null);

        var response = await _client.JudgeAsync(judgmentRequest, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            return (null, response.Failure);
        response.ValidateAgainst(judgmentRequest);
        return (response.Success, null);
    }

    private static List<JudgmentSourceRef> BuildSourceRefs(CaseDecisionRequest request)
    {
        if (request.RecentSegments.Count > 0)
        {
            return request.RecentSegments
                .Select(s => new JudgmentSourceRef(s.ObjectId, s.Sha256, SourceClassification.HostedAllowedSession))
                .ToList();
        }

        // Direct cases persist object refs on user.input (no raw text in the event).
        foreach (var evt in request.RecentEvents)
        {
            if (evt.Type != CaseEventTypes.UserInput) continue;
            if (!evt.Payload.TryGetProperty("objectId", out var idEl) || idEl.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;
            var objectId = idEl.GetString();
            if (string.IsNullOrWhiteSpace(objectId)) continue;
            var sha = evt.Payload.TryGetProperty("sha256", out var shaEl) && shaEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? shaEl.GetString() ?? ""
                : "";
            var classification = evt.Payload.TryGetProperty("classification", out var classEl) && classEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? classEl.GetString() ?? SourceClassification.HostedAllowedSession
                : SourceClassification.HostedAllowedSession;
            return [new JudgmentSourceRef(objectId!, sha, classification)];
        }

        return [];
    }
}
