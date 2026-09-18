using System.Text.RegularExpressions;
using Relay.Core.Cases;
using Relay.Core.Generation;

namespace Relay.Core.Capabilities;

/// <summary>`direct.answer@1` — local evidence answers with citations; no hidden web research.</summary>
public sealed class DirectAnswerCapability : ICapabilityHandler
{
    public const string Id = "direct.answer";
    public const int CapabilityVersion = 1;
    public static string AtVersion => $"{Id}@{CapabilityVersion}";

    private static readonly Regex CurrentWorldHint = new(
        @"\b(today|currently|right now|latest|live|stock price|weather|who won|breaking)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ITextGenerator? _generator;
    private readonly Func<string, IReadOnlyList<EvidenceHit>> _retrieve;

    public DirectAnswerCapability(
        ITextGenerator? generator = null,
        Func<string, IReadOnlyList<EvidenceHit>>? retrieve = null)
    {
        _generator = generator;
        _retrieve = retrieve ?? (_ => Array.Empty<EvidenceHit>());
    }

    public string CapabilityId => Id;
    public int Version => CapabilityVersion;

    public static CapabilityDefinition Definition { get; } = new()
    {
        Id = Id,
        Version = CapabilityVersion,
        AllowedOrigins = [CaseOrigin.Direct],
        SideEffectClass = CapabilitySideEffects.None,
        EvaluationFixtures =
        [
            "DirectAnswer_LocalEvidence_ProducesAnswerWithCitations",
            "DirectAnswer_CurrentWorld_ReturnsLimitation_NotHiddenWebResearch",
            "DirectAnswer_GeneratorUnavailable_ResumableWaitingState",
        ],
        HandlerKey = Id,
    };

    public async Task<CapabilityResult> HandleAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Origin, CaseOrigin.Direct, StringComparison.Ordinal))
        {
            return new CapabilityResult
            {
                Kind = "rejected",
                FeedText = "Direct answer is only available on direct cases.",
                Reason = "origin_not_direct",
                Done = true,
            };
        }

        var objective = request.Objective
            ?? (request.Arguments.TryGetValue("objective", out var o) ? o : "")
            ?? "";

        if (LooksLikeCurrentWorld(objective))
        {
            return new CapabilityResult
            {
                Kind = "limitation",
                FeedText = "I can only answer from local project evidence in v0.1 — not live online or current-world lookup.",
                PresentationLevel = "persistent",
                Done = true,
                Reason = "current_world_limitation",
            };
        }

        var hits = _retrieve(objective);
        if (hits.Count == 0)
        {
            return new CapabilityResult
            {
                Kind = "unresolved",
                FeedText = "No local evidence found for that question.",
                PresentationLevel = "persistent",
                Done = true,
                Reason = "no_local_evidence",
            };
        }

        if (_generator is null)
        {
            return Waiting("generator_unavailable");
        }

        var excerpts = hits.Select(h => h.Excerpt).ToList();
        var gen = await _generator.GenerateAsync(new TextGenerationRequest
        {
            TaskKind = "direct.answer",
            Prompt = objective,
            EvidenceExcerpts = excerpts,
            CaseId = request.CaseId,
        }, cancellationToken).ConfigureAwait(false);

        if (!gen.Ok)
            return Waiting(gen.FailureReason ?? "generator_unavailable");

        var citations = string.Join("; ", hits.Select(h => h.SourceRef));
        return new CapabilityResult
        {
            Kind = "answered",
            FeedText = $"{gen.Text.Trim()} [{citations}]",
            PresentationLevel = "finding",
            Done = true,
            Reason = "local_evidence",
            SourceRefs = hits.Select(h => h.SourceRef).ToList(),
            Artifacts =
            {
                ["answer"] = gen.Text.Trim(),
                ["citations"] = citations,
            },
        };
    }

    private static bool LooksLikeCurrentWorld(string text) =>
        CurrentWorldHint.IsMatch(text);

    private static CapabilityResult Waiting(string reason) => new()
    {
        Kind = "waiting",
        FeedText = "Waiting for local generator.",
        Reason = reason,
        Done = false,
    };
}

public sealed class EvidenceHit
{
    public required string SourceRef { get; init; }
    public required string Excerpt { get; init; }
}
