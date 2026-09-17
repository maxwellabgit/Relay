using System.Text.Json;
using Relay.Core.Listening;
using Relay.Core.Policy;

namespace Relay.Core.Cases;

/// <summary>Routes to different scripted minds by case origin so listening and direct share one runtime.</summary>
public sealed class OriginRoutingMind : ICaseMind
{
    private readonly ICaseMind _observed;
    private readonly ICaseMind _direct;

    public OriginRoutingMind(ICaseMind observed, ICaseMind direct)
    {
        _observed = observed;
        _direct = direct;
    }

    public string Name => $"route({_observed.Name}|{_direct.Name})";

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
        => request.Origin == CaseOrigin.Observed
            ? _observed.StepAsync(request, cancellationToken)
            : _direct.StepAsync(request, cancellationToken);
}

/// <summary>
/// Historical slice-3 harness bridge. Semantic coverage is owned by <see cref="ListeningController"/>;
/// this mind only maps already-captured window text to deterministic moves for old harness scenarios.
/// Keyword heuristics are retained temporarily — see docs/JEV-DECISIONS.md (§9).
/// Production orchestration must not rely on this mind.
/// </summary>
public sealed class ListeningScriptedMind : ICaseMind
{
    private readonly string? _correctionNoteId;
    private readonly string? _correctionProjectId;
    private readonly ListeningController? _listening;
    private readonly Func<string?>? _sessionId;

    public ListeningScriptedMind(
        string? correctionProjectId = null,
        string? correctionNoteId = null,
        ListeningController? listening = null,
        Func<string?>? sessionId = null)
    {
        _correctionProjectId = correctionProjectId;
        _correctionNoteId = correctionNoteId;
        _listening = listening;
        _sessionId = sessionId;
    }

    public string Name => "scripted-listening";

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var awaiting = request.PendingOperations.FirstOrDefault(o =>
            o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing);
        if (awaiting is not null)
        {
            return Task.FromResult(Step(
                "waiting on observed proposal",
                new CaseMove { Type = CaseMove.Wait, Text = "awaiting " + awaiting.OperationId },
                "Waiting on observed proposal."));
        }

        // Prefer older uncovered segments (controller path); never invent coverage without segmentId.
        var segment = SelectNextSegment(request);
        if (segment is null)
        {
            return Task.FromResult(Step(
                "quiet",
                new CaseMove { Type = CaseMove.Wait, Text = "no new segments" },
                "Listening."));
        }

        var text = segment.Text;
        var windowId = TryPendingWindowId(segment.SegmentId);

        if (LooksLikeAcronym(text, out var expansion))
        {
            var say = new CaseMove
            {
                Type = CaseMove.Say,
                Text = expansion,
                Done = false,
                Args = ArgsWithCoverage(segment, windowId, ("attention", "persistent")),
            };
            return Task.FromResult(Step("acronym", say, expansion));
        }

        if (LooksLikeCorrection(text) && _correctionNoteId is not null && _correctionProjectId is not null)
        {
            var body = ExtractCorrectedBody(text);
            var args = ArgsWithCoverage(segment, windowId,
                ("capability", Actions.ModifyNote),
                ("idempotencyKey", "listen-correct-" + segment.SegmentId),
                ("projectId", _correctionProjectId),
                ("noteId", _correctionNoteId),
                ("body", body),
                ("sourceSegmentId", segment.SegmentId));
            return Task.FromResult(Step(
                "correct earlier note",
                new CaseMove
                {
                    Type = CaseMove.Propose,
                    Name = Actions.ModifyNote,
                    Text = "Update the earlier note with the correction.",
                    Args = args,
                },
                "Proposing note correction."));
        }

        if (LooksLikeIdeation(text))
        {
            var objective = "Follow up on: " + TrimTo(text, 160);
            var args = ArgsWithCoverage(segment, windowId,
                ("objective", objective),
                ("kind", CaseKind.Remember),
                ("sourceEventId", segment.EventId));
            return Task.FromResult(Step(
                "raise work from talk",
                new CaseMove
                {
                    Type = CaseMove.RaiseTask,
                    Text = objective,
                    Args = args,
                },
                "Raising a task from the conversation."));
        }

        return Task.FromResult(Step(
            "ambient",
            new CaseMove
            {
                Type = CaseMove.Say,
                Text = "Noted.",
                Done = false,
                Args = ArgsWithCoverage(segment, windowId, ("attention", "ambient")),
            },
            "Ambient note."));
    }

    private ListeningSegmentView? SelectNextSegment(CaseMindRequest request)
    {
        var ordered = request.RecentSegments.OrderBy(s => s.Sequence).ThenBy(s => s.Ts).ToList();
        if (ordered.Count == 0) return null;

        var sessionId = _sessionId?.Invoke() ?? request.CaseId;
        if (_listening is not null)
        {
            // Process oldest pending window's primary segments first.
            var pending = _listening.PendingThroughOutage(sessionId);
            foreach (var window in pending)
            {
                foreach (var id in window.Primary.SegmentIds)
                {
                    var match = ordered.FirstOrDefault(s => s.SegmentId == id);
                    if (match is not null) return match;
                }
            }
            // All windows completed/absent — nothing to process.
            return null;
        }

        // Bridge without injected controller: oldest segment (explicit coverage still required on moves).
        return ordered[0];
    }

    private string? TryPendingWindowId(string segmentId)
    {
        if (_listening is null) return null;
        var sessionId = _sessionId?.Invoke();
        if (sessionId is null) return null;
        return _listening.Store.ListPending(sessionId)
            .FirstOrDefault(w => w.Primary.ContainsSegment(segmentId))
            ?.WindowId;
    }

    private static Dictionary<string, JsonElement> ArgsWithCoverage(
        ListeningSegmentView segment,
        string? windowId,
        params (string Key, string Value)[] extras)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["segmentId"] = JsonSerializer.SerializeToElement(segment.SegmentId),
            ["sourceEventId"] = JsonSerializer.SerializeToElement(segment.EventId),
        };
        if (windowId is not null)
            args["windowId"] = JsonSerializer.SerializeToElement(windowId);
        foreach (var (key, value) in extras)
            args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    private static CaseMindStep Step(string intent, CaseMove move, string feed) => new(
        new CaseMindRead(intent, 0.5, 0.4, 0.2, new CaseMindConfidence(0.8, 0.8, 0.7), []),
        move,
        feed);

    private static bool LooksLikeAcronym(string text, out string expansion)
    {
        expansion = "";
        var lower = text.ToLowerInvariant();
        if (lower.Contains(" means ", StringComparison.Ordinal) || lower.Contains(" stands for ", StringComparison.Ordinal))
        {
            var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].Length <= 6 && parts[0].All(char.IsLetter))
            {
                expansion = text.Trim();
                return true;
            }
        }
        return false;
    }

    private static bool LooksLikeIdeation(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("we should", StringComparison.Ordinal)
            || lower.Contains("let's", StringComparison.Ordinal)
            || lower.Contains("idea:", StringComparison.Ordinal)
            || lower.Contains("what if", StringComparison.Ordinal);
    }

    private static bool LooksLikeCorrection(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("actually", StringComparison.Ordinal)
            || lower.Contains("correction:", StringComparison.Ordinal)
            || lower.Contains("i meant", StringComparison.Ordinal)
            || lower.Contains("not october", StringComparison.Ordinal);
    }

    private static string ExtractCorrectedBody(string text)
    {
        var markers = new[] { "correction:", "actually,", "actually ", "i meant " };
        var lower = text.ToLowerInvariant();
        foreach (var m in markers)
        {
            var i = lower.IndexOf(m, StringComparison.Ordinal);
            if (i >= 0) return text[(i + m.Length)..].Trim();
        }
        return text.Trim();
    }

    private static string TrimTo(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Minimal direct mind used alongside listening: answers briefly or waits.</summary>
public sealed class SimpleDirectMind : ICaseMind
{
    public string Name => "scripted-direct-simple";

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var awaiting = request.PendingOperations.FirstOrDefault(o =>
            o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved);
        if (awaiting is not null)
        {
            return Task.FromResult(new CaseMindStep(
                new CaseMindRead("wait", 0.3, 0.3, 0, new CaseMindConfidence(1, 1, 1), []),
                new CaseMove { Type = CaseMove.Wait, Text = "awaiting " + awaiting.OperationId },
                "Waiting."));
        }

        var answer = "Direct answer: " + (request.Objective ?? "");
        return Task.FromResult(new CaseMindStep(
            new CaseMindRead("answer", 0.6, 0.5, 0, new CaseMindConfidence(0.9, 0.8, 0.8), []),
            new CaseMove { Type = CaseMove.Say, Text = answer, Done = true },
            "Answering direct ask."));
    }
}
