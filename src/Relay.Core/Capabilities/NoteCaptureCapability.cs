using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Generation;
using Relay.Core.Judgments;
using Relay.Core.Policy;
using Relay.Core.Privacy;

namespace Relay.Core.Capabilities;

/// <summary>`conversation.note.capture@1` — correction proposals and verified drafts.</summary>
public sealed class NoteCaptureCapability : ICapabilityHandler
{
    public const string Id = "conversation.note.capture";
    public const int CapabilityVersion = 1;
    public static string AtVersion => $"{Id}@{CapabilityVersion}";

    private readonly ITextGenerator? _generator;
    private readonly DecisionPolicy _policy;
    private readonly IJudgmentClient? _client;
    private readonly JudgmentLifecycle? _lifecycle;
    private readonly string _model;

    public NoteCaptureCapability(
        ITextGenerator? generator = null,
        DecisionPolicy? policy = null,
        IJudgmentClient? client = null,
        JudgmentLifecycle? lifecycle = null,
        string model = "jev-1.13.0")
    {
        _generator = generator;
        _policy = policy ?? new DecisionPolicy();
        _client = client;
        _lifecycle = lifecycle;
        _model = model;
    }

    public string CapabilityId => Id;
    public int Version => CapabilityVersion;

    public static CapabilityDefinition Definition { get; } = new()
    {
        Id = Id,
        Version = CapabilityVersion,
        AllowedOrigins = [CaseOrigin.Observed, CaseOrigin.Direct],
        SideEffectClass = CapabilitySideEffects.CanonicalWrite,
        EvaluationFixtures =
        [
            "NoteCapture_AtlasCorrection_ProposesModifyNote_WithPriorAndTranscriptSourceRefs",
            "NoteCapture_NoteSupportFail_RetainsVerbatimDraft_DoesNotAcceptGeneratedSentence",
            "NoteCapture_EditApprovalArguments_InvalidatesPriorEnvelopeHash",
        ],
        HandlerKey = Id,
    };

    public async Task<CapabilityResult> HandleAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mode = request.Arguments.TryGetValue("mode", out var m) ? m : "new";
        if (string.Equals(mode, "correction", StringComparison.OrdinalIgnoreCase))
            return HandleCorrection(request);

        return await HandleNewNoteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static CapabilityResult HandleCorrection(CapabilityRequest request)
    {
        var projectId = Required(request, "projectId");
        var noteId = Required(request, "noteId");
        var body = Required(request, "body");
        var priorRef = request.Arguments.TryGetValue("priorSourceRef", out var p) ? p : CaseLocalContext.AtlasSourceEventId;
        var transcriptRef = request.Arguments.TryGetValue("transcriptSourceRef", out var t) ? t : "";
        if (string.IsNullOrWhiteSpace(transcriptRef) && request.SourceRefs.Count > 0)
            transcriptRef = request.SourceRefs[0];

        var sourceRefs = new List<string>();
        if (!string.IsNullOrWhiteSpace(priorRef)) sourceRefs.Add(priorRef);
        if (!string.IsNullOrWhiteSpace(transcriptRef)) sourceRefs.Add(transcriptRef);

        return new CapabilityResult
        {
            Kind = "propose_modify",
            FeedText = "Correction proposal ready for approval.",
            PresentationLevel = "alert",
            Done = false,
            Reason = "correction",
            SourceRefs = sourceRefs,
            Artifacts =
            {
                ["capability"] = Actions.ModifyNote,
                ["projectId"] = projectId,
                ["noteId"] = noteId,
                ["body"] = body,
                ["priorSourceRef"] = priorRef,
                ["transcriptSourceRef"] = transcriptRef,
            },
        };
    }

    private async Task<CapabilityResult> HandleNewNoteAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        var verbatim = request.Arguments.TryGetValue("verbatim", out var v) ? v
            : request.Objective ?? "";
        if (string.IsNullOrWhiteSpace(verbatim))
        {
            return new CapabilityResult
            {
                Kind = "unresolved",
                FeedText = "No source excerpt to capture.",
                Reason = "no_excerpt",
                Done = true,
            };
        }

        string generated = verbatim;
        if (_generator is not null)
        {
            var gen = await _generator.GenerateAsync(new TextGenerationRequest
            {
                TaskKind = "conversation.note.capture",
                Prompt = "Draft a concise note from the excerpts.",
                EvidenceExcerpts = [verbatim],
                CaseId = request.CaseId,
            }, cancellationToken).ConfigureAwait(false);
            if (gen.Ok && !string.IsNullOrWhiteSpace(gen.Text))
                generated = gen.Text.Trim();
        }

        var success = await JudgeSupportAsync(generated, verbatim, request, cancellationToken).ConfigureAwait(false);
        if (success is null)
        {
            return new CapabilityResult
            {
                Kind = "waiting",
                FeedText = "Waiting for note verification.",
                Reason = "waiting_for_judgment",
                Done = false,
            };
        }

        var decision = _policy.ApplyNoteSupport(success, generated, verbatim);
        var body = decision.Arguments.TryGetValue("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
            ? bodyEl.GetString() ?? verbatim
            : verbatim;
        var verified = decision.Reason == "note_support_pass";

        return new CapabilityResult
        {
            Kind = verified ? "draft_verified" : "draft_verbatim",
            FeedText = decision.FeedText,
            PresentationLevel = decision.PresentationLevel,
            Done = true,
            Reason = decision.Reason,
            SourceRefs = request.SourceRefs.ToList(),
            Artifacts =
            {
                ["capability"] = Actions.CreateDraftNote,
                ["body"] = body,
                ["verified"] = verified ? "true" : "false",
            },
        };
    }

    private async Task<JudgmentSuccess?> JudgeSupportAsync(
        string draft,
        string verbatim,
        CapabilityRequest request,
        CancellationToken cancellationToken)
    {
        var set = QuestionSets.NoteSupportV1();
        var state = JudgmentState.FromObject(new
        {
            draft,
            excerpts = new[] { verbatim },
        });
        var judgmentRequest = new JudgmentRequest
        {
            QuestionSetId = set.Id,
            QuestionSetVersion = set.Version,
            Model = _model,
            State = state,
            Questions = set.Questions,
            CaseId = request.CaseId,
        };

        if (_lifecycle is not null)
        {
            var result = await _lifecycle.ExecuteAsync(
                judgmentRequest,
                cancellationToken,
                appendCaseEvent: true,
                purpose: HostedPurposes.NoteVerification,
                sessionId: request.SessionId,
                projectId: request.ProjectId).ConfigureAwait(false);
            return result.Response.Ok ? result.Response.Success : null;
        }

        // Tests only — production composition always provides JudgmentLifecycle.
        if (_client is null) return null;
        var response = await _client.JudgeAsync(judgmentRequest, cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return null;
        response.ValidateAgainst(judgmentRequest);
        return response.Success;
    }

    private static string Required(CapabilityRequest request, string key)
    {
        if (!request.Arguments.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Note capture requires '{key}'.");
        return value;
    }
}
