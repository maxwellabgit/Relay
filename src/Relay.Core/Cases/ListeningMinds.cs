using System.Text.Json;
using Relay.Core.Policy;

namespace Relay.Core.Cases;

/// <summary>Routes to different scripted minds by case origin so listening and direct share one runtime.</summary>
[Obsolete("Characterization and DevHarness only. Production uses CaseMindDecisionAdapter + RelayDecisionEngine.")]
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
/// Slice 3 scripted listening mind: acronym → persistent say; ideation → raise_task;
/// correction → propose modify_note; otherwise wait. Never stops the listening case.
/// </summary>
[Obsolete("Characterization and DevHarness only. Production uses CaseMindDecisionAdapter + RelayDecisionEngine.")]
public sealed class ListeningScriptedMind : ICaseMind
{
    private readonly string? _correctionNoteId;
    private readonly string? _correctionProjectId;
    private readonly HashSet<string> _handledSegmentIds = new(StringComparer.Ordinal);

    public ListeningScriptedMind(string? correctionProjectId = null, string? correctionNoteId = null)
    {
        _correctionProjectId = correctionProjectId;
        _correctionNoteId = correctionNoteId;
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

        var fresh = request.RecentSegments
            .Where(s => !_handledSegmentIds.Contains(s.SegmentId))
            .ToList();
        if (fresh.Count == 0)
        {
            return Task.FromResult(Step(
                "quiet",
                new CaseMove { Type = CaseMove.Wait, Text = "no new segments" },
                "Listening."));
        }

        var segment = fresh[^1];
        _handledSegmentIds.Add(segment.SegmentId);
        var text = segment.Text;

        if (LooksLikeAcronym(text, out var expansion))
        {
            var say = new CaseMove
            {
                Type = CaseMove.Say,
                Text = expansion,
                Done = false,
                Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["attention"] = JsonSerializer.SerializeToElement("persistent"),
                    ["segmentId"] = JsonSerializer.SerializeToElement(segment.SegmentId),
                    ["sourceEventId"] = JsonSerializer.SerializeToElement(segment.EventId),
                },
            };
            return Task.FromResult(Step("acronym", say, expansion));
        }

        if (LooksLikeCorrection(text) && _correctionNoteId is not null && _correctionProjectId is not null)
        {
            var body = ExtractCorrectedBody(text);
            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["capability"] = JsonSerializer.SerializeToElement(Actions.ModifyNote),
                ["idempotencyKey"] = JsonSerializer.SerializeToElement("listen-correct-" + segment.SegmentId),
                ["projectId"] = JsonSerializer.SerializeToElement(_correctionProjectId),
                ["noteId"] = JsonSerializer.SerializeToElement(_correctionNoteId),
                ["body"] = JsonSerializer.SerializeToElement(body),
                ["sourceSegmentId"] = JsonSerializer.SerializeToElement(segment.SegmentId),
            };
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
            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["objective"] = JsonSerializer.SerializeToElement(objective),
                ["kind"] = JsonSerializer.SerializeToElement(CaseKind.Remember),
                ["segmentId"] = JsonSerializer.SerializeToElement(segment.SegmentId),
                ["sourceEventId"] = JsonSerializer.SerializeToElement(segment.EventId),
            };
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

        // Low-signal chatter: ambient acknowledgment, keep listening.
        return Task.FromResult(Step(
            "ambient",
            new CaseMove
            {
                Type = CaseMove.Say,
                Text = "Noted.",
                Done = false,
                Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["attention"] = JsonSerializer.SerializeToElement("ambient"),
                    ["segmentId"] = JsonSerializer.SerializeToElement(segment.SegmentId),
                },
            },
            "Ambient note."));
    }

    private static CaseMindStep Step(string intent, CaseMove move, string feed) => new(
        new CaseMindRead(intent, 0.5, 0.4, 0.2, new CaseMindConfidence(0.8, 0.8, 0.7), []),
        move,
        feed);

    private static bool LooksLikeAcronym(string text, out string expansion)
    {
        expansion = "";
        // e.g. "API means Application Programming Interface"
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
        // Prefer an explicit replacement after "actually" / "correction:"
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
[Obsolete("Characterization and DevHarness only. Production uses CaseMindDecisionAdapter + RelayDecisionEngine.")]
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
